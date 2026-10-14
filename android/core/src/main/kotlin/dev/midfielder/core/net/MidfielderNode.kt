package dev.midfielder.core.net

import dev.midfielder.core.Limits
import dev.midfielder.core.ProtocolException
import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.Fingerprint
import dev.midfielder.core.crypto.Hashes
import dev.midfielder.core.crypto.Hex
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.crypto.PairingProof
import dev.midfielder.core.discovery.Backoff
import dev.midfielder.core.transfer.FileNameSanitizer
import dev.midfielder.core.transfer.FileSink
import dev.midfielder.core.transfer.FileSource
import dev.midfielder.core.transfer.TransferEngine
import dev.midfielder.core.transfer.TransferFailedException
import dev.midfielder.core.wire.Envelopes
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DataOpen
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.FileEntry
import dev.midfielder.proto.Hello
import dev.midfielder.proto.PairRequest
import dev.midfielder.proto.PairResult
import dev.midfielder.proto.TransferAnswer
import dev.midfielder.proto.TransferCancel
import dev.midfielder.proto.TransferOffer
import dev.midfielder.proto.TransferPurpose
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withTimeout
import okio.ByteString.Companion.toByteString
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Semaphore
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/** What the platform layer hears about. Callbacks run on IO threads; keep them short. */
public interface NodeEvents {
    public fun onPaired(device: PairedDevice) {}
    public fun onPeerConnected(session: PeerSession) {}
    public fun onPeerDisconnected(device: PairedDevice, cause: Throwable?) {}
    public suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {}

    /**
     * Decide on an incoming offer (names already sanitized). Return one sink per file to accept,
     * or null to decline. May suspend while the user decides (bounded by [NodeConfig.answerTimeoutMs]).
     */
    public suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink>? = null
    public fun onTransferProgress(transferId: String, transferred: Long, total: Long) {}
    public fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {}
}

public sealed class TransferResult {
    public data class Completed(val locations: List<String>) : TransferResult()
    public data class Failed(val reason: String) : TransferResult()
    public data object Cancelled : TransferResult()
}

/**
 * A Midfielder device on the network: accepts connections ([startListening]), dials paired peers
 * ([connect], [pair]), syncs clipboard items and transfers files.
 *
 * Both platforms can be either role; in the shipped apps Windows listens and Android dials, so the
 * phone never needs an open port and never advertises itself.
 */
public class MidfielderNode(
    private val identity: DeviceIdentity,
    deviceName: String,
    private val kind: DeviceKind,
    private val trustStore: TrustStore,
    private val events: NodeEvents,
    private val config: NodeConfig = NodeConfig(),
) : Closeable {
    private val deviceName = sanitizeName(deviceName)
    private val random = SecureRandom()
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler { _, _ -> /* failures surface via events */ },
    )
    private val sessions = ConcurrentHashMap<Fingerprint, PeerSession>()
    private val outgoing = ConcurrentHashMap<String, OutgoingTransfer>()
    private val incoming = ConcurrentHashMap<String, IncomingTransfer>()
    private val pendingAnswers = ConcurrentHashMap<String, CompletableDeferred<TransferAnswer>>()
    private val connectionSlots = Semaphore(config.maxConnections)
    private val _connected = MutableStateFlow<List<PairedDevice>>(emptyList())

    @Volatile
    private var pairing: PairingWindow? = null

    @Volatile
    private var serverSocket: ServerSocket? = null

    private val serverContext by lazy {
        Tls.context(identity) { fp -> trustStore.isTrusted(fp) || pairing?.isOpen(config.clock()) == true }
    }

    public val fingerprint: Fingerprint get() = identity.fingerprint
    public val connectedPeers: StateFlow<List<PairedDevice>> = _connected.asStateFlow()

    public fun session(fp: Fingerprint): PeerSession? = sessions[fp]?.takeIf { it.isOpen }

    // ---------------------------------------------------------------------------------------------
    // Listener role
    // ---------------------------------------------------------------------------------------------

    /** Starts accepting connections. Returns the bound port (pass 0 to pick a free one). */
    public fun startListening(port: Int, bindAddress: InetAddress? = null): Int {
        check(serverSocket == null) { "already listening" }
        val server = serverContext.serverSocketFactory.createServerSocket(port, 50, bindAddress) as SSLServerSocket
        server.enabledProtocols = arrayOf("TLSv1.3")
        server.needClientAuth = true
        serverSocket = server
        scope.launch {
            while (true) {
                val socket = try {
                    runInterruptible { server.accept() }
                } catch (e: SocketException) {
                    break // server closed
                }
                if (!connectionSlots.tryAcquire()) {
                    runCatching { socket.close() }
                    continue
                }
                scope.launch {
                    try {
                        handleInbound(socket as SSLSocket)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: Exception) {
                        runCatching { socket.close() }
                    } finally {
                        connectionSlots.release()
                    }
                }
            }
        }
        return server.localPort
    }

    /**
     * Opens a one-time pairing window and returns the invite to show as a QR code.
     * The window closes after one attempt (successful or not) or after [ttlMs].
     */
    public fun openPairing(addresses: List<HostPort>, ttlMs: Long = 5 * 60_000L): PairingInvite {
        val invite = PairingInvite(identity.fingerprint, PairingInvite.newSecret(random), addresses, deviceName)
        pairing = PairingWindow(invite.secret, config.clock() + ttlMs)
        return invite
    }

    public fun closePairing() {
        pairing = null
    }

    private suspend fun handleInbound(socket: SSLSocket) {
        socket.soTimeout = config.handshakeTimeoutMs
        socket.tcpNoDelay = true
        runInterruptible { socket.startHandshake() }
        val peerFp = Tls.peerFingerprint(socket)
        val input = BufferedInputStream(socket.inputStream, IO_BUFFER)
        val output = BufferedOutputStream(socket.outputStream, IO_BUFFER)
        val first = runInterruptible { Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "first frame") }
        when {
            first.hello != null -> acceptControl(socket, input, output, peerFp, first.hello)
            first.data_open != null -> {
                if (!trustStore.isTrusted(peerFp)) throw ProtocolException("data connection from unpaired peer")
                socket.soTimeout = config.dataReadTimeoutMs
                closingOnCancel(socket) { serveData(input, output, peerFp, first.data_open) }
                socket.close()
            }
            else -> throw ProtocolException("unexpected first frame")
        }
    }

    private suspend fun acceptControl(
        socket: SSLSocket,
        input: InputStream,
        output: OutputStream,
        peerFp: Fingerprint,
        hello: Hello,
    ) {
        validateHello(hello, peerFp)
        val remote = HostPort(socket.inetAddress.hostAddress ?: "unknown", socket.port)
        var device = trustStore.get(peerFp)
        if (device == null) {
            val window = pairing?.takeIf { it.isOpen(config.clock()) }
                ?: throw ProtocolException("unpaired peer and no pairing window")
            pairing = null // single use, whatever the outcome
            val request = runInterruptible {
                Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "PairRequest")
            }.pair_request ?: throw ProtocolException("expected PairRequest")
            val expected = PairingProof.client(window.secret, peerFp, identity.fingerprint)
            if (!Hashes.constantTimeEquals(expected, request.proof.toByteArray())) {
                writeNow(output, Envelope(pair_result = PairResult(accepted = false, reason = "wrong pairing code")))
                throw ProtocolException("bad pairing proof")
            }
            device = PairedDevice(peerFp, sanitizeName(hello.device_name), hello.kind, null)
            trustStore.put(device)
            val proof = PairingProof.server(window.secret, peerFp, identity.fingerprint)
            writeNow(output, Envelope(pair_result = PairResult(accepted = true, proof = proof.toByteString())))
            events.onPaired(device)
        } else {
            device = device.copy(name = sanitizeName(hello.device_name), kind = hello.kind)
            trustStore.put(device)
        }
        writeNow(output, Envelope(hello = myHello()))
        socket.soTimeout = 0
        val session = startSession(socket, input, output, device, isDialer = false, remote = remote)
        session.closed.await()
    }

    // ---------------------------------------------------------------------------------------------
    // Dialer role
    // ---------------------------------------------------------------------------------------------

    /** First-time pairing from a scanned invite. On success the device is trusted and connected. */
    public suspend fun pair(invite: PairingInvite): PeerSession {
        var last: Exception? = null
        for (address in invite.addresses) {
            try {
                return dial(address, invite.fingerprint, invite)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                last = e
            }
        }
        throw IOException("could not reach ${invite.name}", last)
    }

    /** Connects to an already-paired device. Replaces any existing session with it. */
    public suspend fun connect(device: PairedDevice, address: HostPort): PeerSession {
        check(trustStore.isTrusted(device.fingerprint)) { "device is not paired" }
        return dial(address, device.fingerprint, null)
    }

    private suspend fun dial(address: HostPort, expected: Fingerprint, invite: PairingInvite?): PeerSession {
        val socket = openTls(address, expected)
        try {
            socket.soTimeout = config.handshakeTimeoutMs
            val input = BufferedInputStream(socket.inputStream, IO_BUFFER)
            val output = BufferedOutputStream(socket.outputStream, IO_BUFFER)
            val (peerHello, device) = closingOnCancel(socket) {
                Envelopes.write(output, Envelope(hello = myHello()), Limits.MAX_HANDSHAKE_FRAME_BYTES)
                if (invite != null) {
                    val proof = PairingProof.client(invite.secret, identity.fingerprint, expected)
                    Envelopes.write(
                        output, Envelope(pair_request = PairRequest(proof = proof.toByteString())),
                        Limits.MAX_HANDSHAKE_FRAME_BYTES,
                    )
                }
                output.flush()
                if (invite != null) {
                    val result = Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "PairResult").pair_result
                        ?: throw ProtocolException("expected PairResult")
                    if (!result.accepted) throw IOException("pairing refused: ${result.reason.take(100)}")
                    val want = PairingProof.server(invite.secret, identity.fingerprint, expected)
                    if (!Hashes.constantTimeEquals(want, result.proof.toByteArray())) {
                        throw ProtocolException("peer could not prove it knows the pairing code")
                    }
                }
                val hello = Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "Hello").hello
                    ?: throw ProtocolException("expected Hello")
                validateHello(hello, expected)
                hello to PairedDevice(expected, sanitizeName(hello.device_name), hello.kind, address.toString())
            }
            trustStore.put(device)
            if (invite != null) events.onPaired(device)
            socket.soTimeout = 0
            check(peerHello.protocol_version >= 1)
            return startSession(socket, input, output, device, isDialer = true, remote = address)
        } catch (e: Throwable) {
            runCatching { socket.close() }
            throw e
        }
    }

    private suspend fun openTls(address: HostPort, expected: Fingerprint): SSLSocket {
        val ctx = Tls.context(identity) { it == expected }
        val raw = Socket()
        try {
            raw.tcpNoDelay = true
            raw.keepAlive = true
            closingOnCancel(raw) { raw.connect(InetSocketAddress(address.host, address.port), config.connectTimeoutMs) }
            val ssl = ctx.socketFactory.createSocket(raw, address.host, address.port, true) as SSLSocket
            Tls.configure(ssl)
            ssl.useClientMode = true
            ssl.soTimeout = config.handshakeTimeoutMs
            closingOnCancel(ssl) { ssl.startHandshake() }
            return ssl
        } catch (e: Throwable) {
            runCatching { raw.close() }
            throw e
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Sessions
    // ---------------------------------------------------------------------------------------------

    private fun startSession(
        socket: SSLSocket,
        input: InputStream,
        output: OutputStream,
        device: PairedDevice,
        isDialer: Boolean,
        remote: HostPort,
    ): PeerSession {
        val session = PeerSession(socket, input, output, device, isDialer, remote, config, ::dispatch, scope)
        sessions.put(device.fingerprint, session)?.close(IOException("replaced by a new connection"))
        publishConnected()
        session.start()
        scope.launch {
            val cause = session.closed.await()
            sessions.remove(device.fingerprint, session)
            publishConnected()
            events.onPeerDisconnected(device, cause)
        }
        events.onPeerConnected(session)
        // Resume transfers that were interrupted when the previous connection dropped.
        for (t in outgoing.values) {
            if (t.peer == device.fingerprint && t.job == null && !t.result.isCompleted) startOutgoing(session, t)
        }
        return session
    }

    private fun publishConnected() {
        _connected.value = sessions.values.filter { it.isOpen }.map { it.peer }
    }

    private suspend fun dispatch(session: PeerSession, env: Envelope) {
        when {
            env.clipboard != null -> events.onClipboard(session.peer, env.clipboard)
            env.transfer_offer != null -> {
                val offer = env.transfer_offer
                scope.launch { handleOffer(session, offer) }
            }
            env.transfer_answer != null -> pendingAnswers[env.transfer_answer.transfer_id]?.complete(env.transfer_answer)
            env.transfer_cancel != null -> handleCancel(session.peer, env.transfer_cancel.transfer_id)
            else -> Unit // Unknown or out-of-place message: ignore for forward compatibility.
        }
    }

    /** Sends a clipboard item to every connected peer; a slow peer never blocks the others. */
    public fun broadcastClipboard(item: ClipboardItem) {
        val env = Envelope(clipboard = item)
        for (s in sessions.values) if (s.isOpen) s.trySend(env)
    }

    // ---------------------------------------------------------------------------------------------
    // File transfer
    // ---------------------------------------------------------------------------------------------

    private class OutgoingTransfer(
        val id: String,
        val peer: Fingerprint,
        val offer: TransferOffer,
        val sources: List<FileSource>,
    ) {
        val result = CompletableDeferred<TransferResult>()
        val done = BooleanArray(sources.size)

        @Volatile
        var job: Job? = null

        @Synchronized
        fun markDone(i: Int): Boolean {
            done[i] = true
            return done.all { it }
        }

        @Synchronized
        fun isDone(i: Int) = done[i]
    }

    private class IncomingTransfer(
        val id: String,
        val from: PairedDevice,
        val offer: TransferOffer,
        val sinks: List<FileSink>,
        var touchedAt: Long,
    ) {
        val locations = arrayOfNulls<String>(sinks.size)

        @Synchronized
        fun markDone(i: Int, location: String): Boolean {
            locations[i] = location
            return locations.all { it != null }
        }

        @Synchronized
        fun isDone(i: Int) = locations[i] != null
    }

    /**
     * Sends files to a connected peer and suspends until they are delivered, declined or failed.
     * If the connection drops, the transfer resumes (from the last received byte) when the peer reconnects
     * within [NodeConfig.resumeWindowMs].
     */
    public suspend fun sendFiles(
        peer: Fingerprint,
        sources: List<FileSource>,
        purpose: TransferPurpose = TransferPurpose.TRANSFER_PURPOSE_FILE,
    ): TransferResult {
        require(sources.size in 1..Limits.MAX_FILES_PER_OFFER) { "1..${Limits.MAX_FILES_PER_OFFER} files" }
        val session = session(peer) ?: throw NotConnectedException("peer not connected")
        val id = Hex.encode(ByteArray(Limits.TRANSFER_ID_HEX_CHARS / 2).also { random.nextBytes(it) })
        val offer = TransferOffer(
            transfer_id = id,
            files = sources.map {
                FileEntry(
                    name = FileNameSanitizer.sanitize(it.name), size = it.size, mime = it.mime.take(100),
                    modified_ms = it.modifiedMs,
                )
            },
            purpose = purpose,
        )
        val t = OutgoingTransfer(id, peer, offer, sources)
        outgoing[id] = t
        try {
            startOutgoing(session, t)
            return t.result.await()
        } finally {
            outgoing.remove(id)
            t.job?.cancel()
        }
    }

    private fun startOutgoing(session: PeerSession, t: OutgoingTransfer) {
        t.job = scope.launch {
            try {
                val answer = requestAnswer(session, t.offer)
                if (!answer.accepted) {
                    t.result.complete(TransferResult.Failed("declined: ${answer.reason.take(100)}"))
                    return@launch
                }
                if (session.isDialer) {
                    for (i in t.sources.indices) {
                        if (t.isDone(i)) continue
                        runDataAsDialer(session, t.id, i) { input, output ->
                            TransferEngine.send(input, output, t.sources[i], t.offer.files[i], progressFor(t.id))
                        }
                        t.markDone(i)
                    }
                    t.result.complete(TransferResult.Completed(emptyList()))
                } else {
                    // The peer dials data connections to us; serveData() completes t.result.
                    select<Unit> {
                        t.result.onAwait { }
                        session.closed.onAwait { throw IOException("disconnected") }
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (session.isOpen && e !is NotConnectedException) {
                    t.result.complete(TransferResult.Failed(e.message ?: "transfer failed"))
                } else {
                    pauseOutgoing(t)
                }
            }
        }
    }

    /** Waits up to the resume window for the peer to come back; startSession() restarts the transfer. */
    private fun pauseOutgoing(t: OutgoingTransfer) {
        t.job = null
        scope.launch {
            delay(config.resumeWindowMs)
            if (t.job == null) t.result.complete(TransferResult.Failed("peer disconnected"))
        }
    }

    private suspend fun requestAnswer(session: PeerSession, offer: TransferOffer): TransferAnswer {
        val deferred = CompletableDeferred<TransferAnswer>()
        pendingAnswers[offer.transfer_id] = deferred
        try {
            session.send(Envelope(transfer_offer = offer))
            return withTimeout(config.answerTimeoutMs) {
                select {
                    deferred.onAwait { it }
                    session.closed.onAwait { throw NotConnectedException("disconnected while waiting for answer") }
                }
            }
        } finally {
            pendingAnswers.remove(offer.transfer_id, deferred)
        }
    }

    private suspend fun handleOffer(session: PeerSession, raw: TransferOffer) {
        val id = raw.transfer_id
        val from = session.peer
        val valid = Hex.isLowerHex(id, Limits.TRANSFER_ID_HEX_CHARS) &&
            raw.files.size in 1..Limits.MAX_FILES_PER_OFFER &&
            raw.files.all { it.size >= 0 }
        if (!valid) {
            session.trySend(Envelope(transfer_answer = TransferAnswer(id.take(64), false, "invalid offer")))
            return
        }
        val existing = incoming[id]
        if (existing != null) {
            // A resumed transfer: same id from the same peer.
            val ok = existing.from.fingerprint == from.fingerprint
            existing.touchedAt = config.clock()
            session.send(Envelope(transfer_answer = TransferAnswer(id, ok, if (ok) "" else "id in use")))
            if (ok && session.isDialer) startIncomingAsDialer(session, existing)
            return
        }
        sweepStaleIncoming()
        val offer = raw.copy(files = raw.files.map { it.copy(name = FileNameSanitizer.sanitize(it.name)) })
        val sinks = try {
            withTimeout(config.answerTimeoutMs) { events.onTransferOffer(from, offer) }
        } catch (e: CancellationException) {
            if (!session.isOpen) throw e
            null
        }
        if (sinks == null || sinks.size != offer.files.size) {
            session.send(Envelope(transfer_answer = TransferAnswer(id, false, "declined")))
            return
        }
        val t = IncomingTransfer(id, from, offer, sinks, config.clock())
        incoming[id] = t
        session.send(Envelope(transfer_answer = TransferAnswer(id, true, "")))
        if (session.isDialer) startIncomingAsDialer(session, t)
    }

    private fun startIncomingAsDialer(session: PeerSession, t: IncomingTransfer) {
        scope.launch {
            try {
                for (i in t.sinks.indices) {
                    if (t.isDone(i)) continue
                    runDataAsDialer(session, t.id, i) { input, output ->
                        val location = TransferEngine.receive(input, output, t.sinks[i], t.offer.files[i], progressFor(t.id))
                        t.markDone(i, location)
                    }
                }
                finishIncoming(t, null)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Peer gone: keep partial data, the sender re-offers the same id when it reconnects.
                if (session.isOpen) finishIncoming(t, e.message ?: "transfer failed")
            }
        }
    }

    private fun finishIncoming(t: IncomingTransfer, failure: String?) {
        if (!incoming.remove(t.id, t)) return
        if (failure == null) {
            events.onTransferFinished(t.id, t.from, TransferResult.Completed(t.locations.filterNotNull()))
        } else {
            t.sinks.forEachIndexed { i, s -> if (!t.isDone(i)) runCatching { s.discard() } }
            events.onTransferFinished(t.id, t.from, TransferResult.Failed(failure))
        }
    }

    /** Lazy cleanup instead of a timer: nothing wakes the CPU just to tidy up. */
    private fun sweepStaleIncoming() {
        val now = config.clock()
        for (t in incoming.values) {
            if (now - t.touchedAt > config.resumeWindowMs) finishIncoming(t, "expired")
        }
    }

    /** Listener side of a data connection opened by the peer. */
    private fun serveData(input: InputStream, output: OutputStream, peerFp: Fingerprint, open: DataOpen) {
        val index = open.file_index
        val out = outgoing[open.transfer_id]
        if (out != null && out.peer == peerFp) {
            if (index !in out.sources.indices) throw ProtocolException("bad file index")
            TransferEngine.send(input, output, out.sources[index], out.offer.files[index], progressFor(out.id))
            if (out.markDone(index)) out.result.complete(TransferResult.Completed(emptyList()))
            return
        }
        val inc = incoming[open.transfer_id]
        if (inc != null && inc.from.fingerprint == peerFp) {
            if (index !in inc.sinks.indices) throw ProtocolException("bad file index")
            inc.touchedAt = config.clock()
            val location = TransferEngine.receive(input, output, inc.sinks[index], inc.offer.files[index], progressFor(inc.id))
            if (inc.markDone(index, location)) finishIncoming(inc, null)
            return
        }
        throw ProtocolException("unknown transfer")
    }

    private suspend fun runDataAsDialer(
        session: PeerSession,
        transferId: String,
        index: Int,
        block: (InputStream, OutputStream) -> Unit,
    ) {
        val backoff = Backoff(initialMs = 1_000, maxMs = 15_000)
        var attempt = 0
        while (true) {
            try {
                val socket = openTls(session.remote, session.peer.fingerprint)
                socket.use {
                    socket.soTimeout = config.dataReadTimeoutMs
                    val input = BufferedInputStream(socket.inputStream, IO_BUFFER)
                    val output = BufferedOutputStream(socket.outputStream, IO_BUFFER)
                    closingOnCancel(socket) {
                        Envelopes.write(
                            output, Envelope(data_open = DataOpen(transferId, index)), Limits.MAX_HANDSHAKE_FRAME_BYTES,
                        )
                        output.flush()
                        block(input, output)
                    }
                }
                return
            } catch (e: CancellationException) {
                throw e
            } catch (e: IOException) {
                attempt++
                if (attempt >= config.dataRetryAttempts || !session.isOpen) throw e
                if (e is TransferFailedException) backoff.reset() // corrupt data was discarded; retry from 0
                delay(backoff.nextDelayMs())
            }
        }
    }

    /** Cancels a transfer in either direction and tells the peer. */
    public fun cancelTransfer(transferId: String) {
        outgoing[transferId]?.let { t ->
            t.result.complete(TransferResult.Cancelled)
            t.job?.cancel()
            session(t.peer)?.trySend(Envelope(transfer_cancel = TransferCancel(transferId, "cancelled")))
        }
        incoming[transferId]?.let { t ->
            session(t.from.fingerprint)?.trySend(Envelope(transfer_cancel = TransferCancel(transferId, "cancelled")))
            cancelIncoming(t)
        }
    }

    private fun handleCancel(from: PairedDevice, transferId: String) {
        outgoing[transferId]?.takeIf { it.peer == from.fingerprint }?.let { t ->
            t.result.complete(TransferResult.Cancelled)
            t.job?.cancel()
        }
        incoming[transferId]?.takeIf { it.from.fingerprint == from.fingerprint }?.let(::cancelIncoming)
    }

    private fun cancelIncoming(t: IncomingTransfer) {
        if (!incoming.remove(t.id, t)) return
        t.sinks.forEachIndexed { i, s -> if (!t.isDone(i)) runCatching { s.discard() } }
        events.onTransferFinished(t.id, t.from, TransferResult.Cancelled)
    }

    private fun progressFor(id: String) = TransferEngine.Progress { done, total ->
        events.onTransferProgress(id, done, total)
    }

    // ---------------------------------------------------------------------------------------------

    private fun myHello() = Hello(
        device_id = identity.deviceId,
        device_name = deviceName,
        kind = kind,
        protocol_version = Limits.PROTOCOL_VERSION,
        capabilities = CAPABILITIES,
    )

    private fun validateHello(hello: Hello, peerFp: Fingerprint) {
        if (hello.device_id != peerFp.deviceId) throw ProtocolException("device id does not match certificate")
        if (hello.protocol_version < 1) throw ProtocolException("unsupported protocol version")
    }

    private fun writeNow(output: OutputStream, env: Envelope) {
        Envelopes.write(output, env, Limits.MAX_HANDSHAKE_FRAME_BYTES)
        output.flush()
    }

    /** Disconnects from a peer and forgets it. */
    public fun unpair(fp: Fingerprint) {
        trustStore.remove(fp)
        sessions[fp]?.close(IOException("unpaired"))
    }

    override fun close() {
        runCatching { serverSocket?.close() }
        for (s in sessions.values) s.close()
        sessions.clear()
        for (t in outgoing.values) t.result.complete(TransferResult.Failed("shutting down"))
        scope.cancel()
    }

    private class PairingWindow(val secret: ByteArray, val expiresAt: Long) {
        fun isOpen(now: Long) = now < expiresAt
    }

    private companion object {
        const val IO_BUFFER = 64 * 1024
        val CAPABILITIES = listOf("clipboard.text", "clipboard.image", "file")

        fun sanitizeName(name: String): String =
            name.filter { it.code >= 0x20 && it.code != 0x7F }.trim().take(Limits.MAX_DEVICE_NAME_CHARS)
                .ifEmpty { "Device" }

        /**
         * Runs blocking socket I/O so that cancelling the coroutine closes [socket] (the only reliable way to
         * unblock a thread stuck in a socket read).
         */
        suspend fun <T> closingOnCancel(socket: Socket, block: () -> T): T = coroutineScope {
            val watcher = launch {
                try {
                    awaitCancellation()
                } finally {
                    // Only close when the caller was cancelled, not when the block finished normally.
                    if (this@coroutineScope.coroutineContext[Job]?.isActive != true) runCatching { socket.close() }
                }
            }
            try {
                runInterruptible(Dispatchers.IO) { block() }
            } finally {
                watcher.cancel()
            }
        }
    }
}
