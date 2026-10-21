package dev.midfielder.core.net

import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.wire.Envelopes
import dev.midfielder.proto.Bye
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.Ping
import dev.midfielder.proto.Pong
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ClosedSendChannelException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

public class NotConnectedException(message: String) : IOException(message)

/**
 * One authenticated control connection to a peer.
 *
 * Exactly three coroutines per session: a reader (blocks in socket read, wakes only when data arrives),
 * a writer (suspends on a bounded queue), and a heartbeat (sleeps for minutes at a time). Closing the
 * session closes the socket, which unblocks the reader, and cancels the scope, so nothing outlives it.
 */
public class PeerSession internal constructor(
    private val socket: Socket,
    private val input: InputStream,
    private val output: OutputStream,
    public val peer: PairedDevice,
    /** True if we opened the connection. The dialer drives heartbeats and opens data connections. */
    public val isDialer: Boolean,
    /** Where the peer's listener is (only meaningful when [isDialer]). */
    public val remote: HostPort,
    private val config: NodeConfig,
    private val handler: suspend (PeerSession, Envelope) -> Unit,
    parent: CoroutineScope,
    capabilities: Set<String> = emptySet(),
) {
    /** What the peer supports and has switched on (from its Hello, replaced by FeaturesChanged). */
    @Volatile
    public var capabilities: Set<String> = capabilities
        internal set

    public fun has(capability: String): Boolean = capability in capabilities

    private val job = SupervisorJob(parent.coroutineContext[Job])
    private val scope = CoroutineScope(parent.coroutineContext + job + Dispatchers.IO)
    private val outgoing = Channel<Envelope>(config.sendQueueCapacity)
    private val lastInboundMs = AtomicLong(config.clock())
    private val closedFlag = AtomicBoolean(false)
    private val closedSignal = CompletableDeferred<Throwable?>()

    public val isOpen: Boolean get() = !closedFlag.get()

    /** Completes with the close cause (null for a clean close). */
    public val closed: Deferred<Throwable?> get() = closedSignal

    internal fun start() {
        scope.launch { writerLoop() }
        scope.launch { readerLoop() }
        scope.launch { heartbeatLoop() }
    }

    /** Queues a message; suspends if the queue is full (backpressure). */
    public suspend fun send(envelope: Envelope) {
        try {
            outgoing.send(envelope)
        } catch (e: ClosedSendChannelException) {
            throw NotConnectedException("session to ${peer.name} is closed")
        }
    }

    /** Queues a message unless the queue is full or closed. For droppable traffic (clipboard, pings). */
    public fun trySend(envelope: Envelope): Boolean = outgoing.trySend(envelope).isSuccess

    /**
     * Checks liveness right now (Android calls this on screen-on / network change, because coroutine
     * timers don't run while the CPU is in deep sleep).
     */
    public fun probe() {
        if (!isOpen) return
        val sentAt = config.clock()
        trySend(Envelope(ping = Ping(sent_at_ms = sentAt)))
        scope.launch {
            delay(config.heartbeatTimeoutMs)
            if (lastInboundMs.get() < sentAt) close(IOException("probe timed out"))
        }
    }

    public fun close(cause: Throwable? = null) {
        if (!closedFlag.compareAndSet(false, true)) return
        outgoing.close()
        runCatching { socket.close() }
        closedSignal.complete(cause)
        scope.cancel()
    }

    /** Sends Bye (best effort, bounded wait) and closes. */
    public suspend fun goodbye(reason: String) {
        if (trySend(Envelope(bye = Bye(reason = reason)))) delay(50)
        close()
    }

    private suspend fun writerLoop() {
        try {
            while (true) {
                val first = outgoing.receiveCatching().getOrNull() ?: break
                Envelopes.write(output, first, config.maxFrameBytes)
                // Drain whatever else is queued, then flush once: fewer syscalls and TLS records.
                while (true) {
                    val next = outgoing.tryReceive().getOrNull() ?: break
                    Envelopes.write(output, next, config.maxFrameBytes)
                }
                output.flush()
            }
        } catch (e: Exception) {
            close(e)
        }
    }

    private suspend fun readerLoop() {
        try {
            while (true) {
                val env = Envelopes.read(input, config.maxFrameBytes) ?: break
                lastInboundMs.set(config.clock())
                when {
                    env.ping != null -> trySend(Envelope(pong = Pong(echo_sent_at_ms = env.ping.sent_at_ms)))
                    env.pong != null -> Unit
                    env.bye != null -> break
                    else -> handler(this, env)
                }
            }
            close(null)
        } catch (e: Exception) {
            close(e)
        }
    }

    private suspend fun heartbeatLoop() {
        val clock = config.clock
        while (scope.isActive) {
            if (isDialer) {
                delay(config.heartbeatIntervalMs)
                if (clock() - lastInboundMs.get() < config.heartbeatIntervalMs) continue
                trySend(Envelope(ping = Ping(sent_at_ms = clock())))
                delay(config.heartbeatTimeoutMs)
                if (clock() - lastInboundMs.get() >= config.heartbeatIntervalMs + config.heartbeatTimeoutMs) {
                    close(IOException("heartbeat timed out"))
                    return
                }
            } else {
                delay(config.listenerIdleTimeoutMs / 2)
                if (clock() - lastInboundMs.get() >= config.listenerIdleTimeoutMs) {
                    close(IOException("peer silent for too long"))
                    return
                }
            }
        }
    }

    override fun toString(): String = "PeerSession(${peer.name}, dialer=$isDialer, open=$isOpen)"
}
