package dev.midfielder.android

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import android.os.SystemClock
import android.provider.Settings
import dev.midfielder.core.clip.ClipContent
import dev.midfielder.core.clip.ClipResult
import dev.midfielder.core.clip.ClipSync
import dev.midfielder.core.clip.ClipboardHistory
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.history.Direction
import dev.midfielder.core.history.RecordStatus
import dev.midfielder.core.history.TransferRecord
import dev.midfielder.core.history.TransferTracker
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.NodeConfig
import dev.midfielder.core.net.NodeEvents
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.PeerSession
import dev.midfielder.core.net.TransferResult
import dev.midfielder.core.transfer.BytesFileSource
import dev.midfielder.core.transfer.DirectoryFileSink
import dev.midfielder.core.transfer.FileSink
import dev.midfielder.core.transfer.FileSource
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.TransferOffer
import dev.midfielder.proto.TransferPurpose
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import java.io.Closeable
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Everything the phone does, owned by [SyncService] and closed with it. Holds only the application context,
 * so no Activity can leak through it. One coroutine scope; closing cancels every job it started.
 */
class SyncEngine(context: Context) : Closeable {
    data class Status(
        val connected: List<String> = emptyList(),
        val paired: List<String> = emptyList(),
        val lastEvent: String = "",
    )

    private val app = context.applicationContext
    // Any unexpected error in background work is logged, never allowed to crash the app.
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, e -> EventLog.error("Unexpected error", e) },
    )
    val storage = AppStorage(app)
    private val identity = storage.loadOrCreateIdentity()
    private val notifications = Notifications(app)
    private val clipboard = ClipboardBridge(app)
    private val locks = TransferLocks(app)
    private val clipTransfers = ConcurrentHashMap.newKeySet<String>()
    private val incomingOffers = ConcurrentHashMap<String, TransferOffer>()
    private val lastProgressUi = ConcurrentHashMap<String, Long>()
    private val _status = MutableStateFlow(Status())
    val status: StateFlow<Status> get() = _status

    @Volatile
    var uiInForeground = false

    /** File history and usage counters (shared with the UI). */
    val history: HistoryStore = HistoryStore.get(app)

    /** Transfers that are moving bytes right now, for progress cards in the UI. */
    val transfers: TransferTracker = TransferTracker(SystemClock::elapsedRealtime)

    val deviceId: String get() = identity.deviceId
    val fingerprint: String get() = identity.fingerprint.display()

    private val events: NodeEvents = object : NodeEvents {
        override fun onPaired(device: PairedDevice) = refreshStatus("Paired with ${device.name}")

        override fun onPeerConnected(session: PeerSession) {
            clipSync.catchUpItem()?.let { session.trySend(Envelope(clipboard = it)) }
            flushWaitingSends()
            features.onPeerConnected(session)
            refreshStatus("Connected to ${session.peer.name}")
        }

        override fun onPeerDisconnected(device: PairedDevice, cause: Throwable?) {
            features.onPeerDisconnected()
            refreshStatus("Disconnected from ${device.name}")
            connections.kick("disconnected")
        }

        override fun onFeature(from: PeerSession, env: Envelope) = features.onMessage(from, env)

        override suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {
            val result = try {
                clipSync.onRemote(item)
            } catch (e: Exception) {
                EventLog.error("Applying clipboard from ${from.name} failed", e)
                return
            }
            EventLog.add("Clipboard from ${from.name}: ${item.kind.name.removePrefix("CLIP_KIND_").lowercase()}, ${item.size} bytes -> $result")
            if (result == ClipResult.APPLIED) history.recordClip(Direction.RECEIVED)
        }

        override suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink>? {
            if (offer.purpose == TransferPurpose.TRANSFER_PURPOSE_CLIPBOARD) {
                if (offer.files.size != 1 || offer.files[0].size > MAX_CLIP_TRANSFER_BYTES) return null
                clipTransfers += offer.transfer_id
                return listOf(DirectoryFileSink(File(app.cacheDir, "clip-in").toPath(), offer.transfer_id, "clipboard.png"))
            }
            if (!storage.acceptFiles) return null
            locks.acquire()
            incomingOffers[offer.transfer_id] = offer
            transfers.start(offer.transfer_id, Direction.RECEIVED, titleOf(offer.files.map { it.name }), from.name, offer.files.sumOf { it.size })
            return offer.files.mapIndexed { i, f -> MediaStoreFileSink(app, "${offer.transfer_id}-$i", f.name, f.mime) }
        }

        override fun onTransferProgress(transferId: String, transferred: Long, total: Long) {
            // Clipboard-image transfers aren't tracked: they're shown as a clipboard change, not a file.
            if (!transfers.contains(transferId)) return
            transfers.progress(transferId, transferred, total)
            val now = SystemClock.elapsedRealtime()
            if (transferred < total && now - (lastProgressUi[transferId] ?: 0L) < PROGRESS_UI_MS) return
            lastProgressUi[transferId] = now
            val t = transfers.active.value.firstOrNull { it.id == transferId } ?: return
            val verb = if (t.direction == Direction.SENT) "Sending" else "Receiving"
            notifications.progress(transferId, "$verb ${t.title}", transferred, total)
        }

        override fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {
            lastProgressUi.remove(transferId)
            if (clipTransfers.remove(transferId)) {
                if (result is TransferResult.Completed) applyClipboardFile(result.locations.single())
                return
            }
            locks.release()
            transfers.finish(transferId)
            val offer = incomingOffers.remove(transferId)
            when (result) {
                is TransferResult.Completed -> {
                    val records = offer?.let { receivedRecords(transferId, it, from, result.locations) }.orEmpty()
                    history.add(records)
                    history.recordFiles(Direction.RECEIVED, records.size, records.sumOf { it.size })
                    EventLog.add("Received ${records.size} file(s) from ${from.name}")
                    notifications.finished(
                        transferId, "Received from ${from.name}",
                        records.singleOrNull()?.name ?: "${records.size} files in Download/Midfielder",
                        openRecordId = records.singleOrNull()?.id,
                    )
                }
                is TransferResult.Failed -> {
                    EventLog.add("Transfer from ${from.name} failed: ${result.reason}")
                    notifications.finished(transferId, "Transfer from ${from.name} failed", result.reason)
                }
                TransferResult.Cancelled -> notifications.cancel(transferId)
            }
        }
    }

    private val node: MidfielderNode = MidfielderNode(
        identity, deviceName(), DeviceKind.DEVICE_KIND_ANDROID, storage.trustStore, events,
        // elapsedRealtime keeps counting in deep sleep, so heartbeat math stays right after the phone naps.
        NodeConfig(clock = SystemClock::elapsedRealtime),
    )

    /** Notifications, battery, find my phone and music; each switched on or off in Settings. */
    val features: PhoneFeatures by lazy { PhoneFeatures(app, node, storage, notifications) }

    val clipSync: ClipSync = ClipSync(
        identity.deviceId, scope,
        broadcast = {
            val n = node.broadcastClipboard(it)
            EventLog.add("Clipboard sent to $n PC(s)")
            if (n > 0) history.recordClip(Direction.SENT)
        },
        apply = { content, _ -> applyToClipboard(content) },
        history = ClipboardHistory(maxItems = 50, maxTotalBytes = 8L * 1024 * 1024),
    ).apply { enabled = storage.syncClipboard }

    private val connections: ConnectionManager = ConnectionManager(app, node, storage.trustStore, scope)
    private val triggers: CaptureTriggers = CaptureTriggers(app, scope) { source ->
        EventLog.add("Copy detected ($source)")
        requestClipboardRead()
    }

    val captureMode: String
        get() = when {
            triggers.logAccessActive -> "Automatic (log access approved)"
            triggers.hasReadLogs && !isAccessibilityOn() -> "Needs approval: reopen Midfielder and tap \"Allow\" when Android asks"
            isAccessibilityOn() -> "Automatic (accessibility)"
            else -> "Tap \"Send clipboard\" (or enable automatic mode)"
        }

    fun start() {
        connections.onScreenChanged = { on ->
            if (on) for (d in node.connectedPeers.value) node.session(d.fingerprint)?.probe()
        }
        features.apply()
        connections.start()
        triggers.start()
        ShareShortcuts.update(app, storage.trustStore.all())
        refreshStatus("Started")
    }

    fun statusText(): String {
        val s = _status.value
        val needsApproval = triggers.hasReadLogs && !triggers.logAccessActive && !isAccessibilityOn()
        return when {
            s.connected.isNotEmpty() && needsApproval ->
                "Connected to ${s.connected.joinToString()} · open Midfielder once to turn on automatic copy"
            s.connected.isNotEmpty() -> "Connected to ${s.connected.joinToString()}"
            s.paired.isNotEmpty() -> "Waiting for ${s.paired.joinToString()}"
            else -> "Not paired yet: open Midfielder to pair with your PC"
        }
    }


    suspend fun pair(invite: PairingInvite): PairedDevice {
        val session = node.pair(invite)
        storage.trustStore.put(session.peer.copy(lastAddress = session.remote.toString()))
        ShareShortcuts.update(app, storage.trustStore.all())
        refreshStatus("Paired with ${session.peer.name}")
        return session.peer
    }

    fun unpairAll() {
        for (d in storage.trustStore.all()) node.unpair(d.fingerprint)
        ShareShortcuts.update(app, storage.trustStore.all())
        refreshStatus("Forgot paired PCs")
    }

    /** True once Android approved log access, so copies are detected without any tap. */
    val logDetectionActive: Boolean get() = triggers.logAccessActive

    /** Midfielder's screen came to the front: the moment Android can show the log-access prompt. */
    fun onUiResumed() {
        uiInForeground = true
        triggers.ensureLogReader(inForeground = true)
    }

    /** A trigger fired: read the clipboard now (directly if our UI is in front, else via the reader activity). */
    fun requestClipboardRead() {
        if (!clipSync.enabled || clipboard.isSuppressed || storage.trustStore.all().isEmpty()) return
        if (uiInForeground) {
            clipboard.read()?.let(::onLocalClipboard)
            return
        }
        val canStartFromBackground = Settings.canDrawOverlays(app) || isAccessibilityOn()
        if (!canStartFromBackground) {
            EventLog.add("Can't read the clipboard in the background: allow \"Display over other apps\"")
            return
        }
        runCatching {
            app.startActivity(
                Intent(app, ClipboardReaderActivity::class.java).addFlags(
                    Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION or Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS,
                ),
            )
        }
    }

    /**
     * Called by the reader activity, the share sheet and the UI with fresh clipboard content.
     * Returns a short, user-facing description of what happened.
     */
    fun onLocalClipboard(content: ClipContent, force: Boolean = false): String {
        val what = describe(content)
        val peers = node.connectedPeers.value
        if (!clipSync.isInlineSize(content)) {
            if (content !is ClipContent.Image) return "Too large to sync".also { EventLog.add("Local copy: $what -> too large") }
            EventLog.add("Local copy: $what -> sending as a transfer")
            sendToPcs(listOf(BytesFileSource("clipboard.png", content.png, "image/png")), TransferPurpose.TRANSFER_PURPOSE_CLIPBOARD)
            return if (peers.isEmpty()) "PC not connected" else "Sending image to ${peers.first().name}"
        }
        val result = clipSync.onLocalChange(content, force)
        EventLog.add("${if (force) "Send now" else "Local copy"}: $what -> $result")
        if (result == ClipResult.SENT && peers.isEmpty()) connections.kick("clipboard")
        return when {
            result == ClipResult.DISABLED -> "Clipboard sync is turned off"
            result == ClipResult.DUPLICATE -> "Already sent (nothing new on the clipboard)"
            result != ClipResult.SENT -> "Couldn't send ($result)"
            peers.isEmpty() -> "PC not connected (will send when it reconnects)"
            else -> "Sent to ${peers.joinToString { it.name }}"
        }
    }

    private fun describe(c: ClipContent): String = when (c) {
        is ClipContent.Text -> "text, ${c.text.length} chars${if (c.sensitive) ", sensitive" else ""}"
        is ClipContent.Image -> "image, ${c.png.size / 1024} KB"
    }

    /** Sends files to [targetShortcutId]'s PC (from Direct Share) or the first connected PC. */
    fun sendUris(uris: List<Uri>, targetShortcutId: String? = null) {
        val sources = uris.mapNotNull { uri ->
            runCatching { UriFileSource(app, uri) }.onFailure { EventLog.error("Can't read $uri", it) }.getOrNull()
        }
        if (sources.isNotEmpty()) sendToPcs(sources, TransferPurpose.TRANSFER_PURPOSE_FILE, targetShortcutId)
    }

    fun cancelTransfer(id: String) = node.cancelTransfer(id)

    private fun sendToPcs(sources: List<FileSource>, purpose: TransferPurpose, targetShortcutId: String? = null) {
        val peers = node.connectedPeers.value
        val target = peers.firstOrNull { ShareShortcuts.idFor(it) == targetShortcutId } ?: peers.firstOrNull()
        if (target == null) {
            // Typical right after a share starts the service: hold the files briefly and send once connected.
            if (purpose == TransferPurpose.TRANSFER_PURPOSE_FILE) {
                synchronized(waitingSends) { waitingSends += WaitingSend(sources, targetShortcutId, SystemClock.elapsedRealtime()) }
                EventLog.add("PC not connected: ${sources.size} file(s) will be sent when it connects")
            }
            refreshStatus("PC not connected")
            connections.kick("send")
            return
        }
        val isFile = purpose == TransferPurpose.TRANSFER_PURPOSE_FILE
        val id = node.newTransferId()
        if (isFile) transfers.start(id, Direction.SENT, titleOf(sources.map { it.name }), target.name, sources.sumOf { it.size })
        scope.launch {
            locks.acquire()
            try {
                val result = node.sendFiles(target.fingerprint, sources, purpose, id)
                if (isFile) onSendFinished(id, target, sources, result)
            } catch (e: Exception) {
                if (isFile) onSendFinished(id, target, sources, TransferResult.Failed(e.message ?: "send failed"))
                refreshStatus("Send failed: ${e.message}")
            } finally {
                locks.release()
            }
        }
    }

    private class WaitingSend(val sources: List<FileSource>, val target: String?, val atMs: Long)

    private val waitingSends = ArrayList<WaitingSend>()

    private fun flushWaitingSends() {
        val now = SystemClock.elapsedRealtime()
        val ready = synchronized(waitingSends) { waitingSends.toList().also { waitingSends.clear() } }
        for (w in ready) {
            if (now - w.atMs <= WAITING_SEND_MS) sendToPcs(w.sources, TransferPurpose.TRANSFER_PURPOSE_FILE, w.target)
            else EventLog.add("Gave up sending ${w.sources.size} file(s): PC didn't connect")
        }
    }

    private fun onSendFinished(id: String, target: PairedDevice, sources: List<FileSource>, result: TransferResult) {
        transfers.finish(id)
        lastProgressUi.remove(id)
        val status = when (result) {
            is TransferResult.Completed -> RecordStatus.DONE
            is TransferResult.Failed -> RecordStatus.FAILED
            TransferResult.Cancelled -> RecordStatus.CANCELLED
        }
        val now = System.currentTimeMillis()
        history.add(
            sources.mapIndexed { i, src ->
                TransferRecord(
                    "$id-$i", Direction.SENT, src.name, src.mime, src.size, target.name, now, status,
                    (src as? UriFileSource)?.uri?.toString().orEmpty(),
                )
            },
        )
        if (status == RecordStatus.DONE) history.recordFiles(Direction.SENT, sources.size, sources.sumOf { it.size })
        EventLog.add("Send ${sources.size} file(s) to ${target.name}: $status")
        when (result) {
            is TransferResult.Completed -> notifications.finished(id, "Sent to ${target.name}", titleOf(sources.map { it.name }))
            is TransferResult.Failed -> notifications.finished(id, "Not sent", result.reason)
            TransferResult.Cancelled -> notifications.cancel(id)
        }
    }

    private fun receivedRecords(id: String, offer: TransferOffer, from: PairedDevice, locations: List<String>): List<TransferRecord> {
        val now = System.currentTimeMillis()
        return offer.files.zip(locations).mapIndexed { i, (f, location) ->
            // MediaStore may have renamed it ("photo (1).jpg") to avoid a clash.
            val name = runCatching { MediaStoreFileSink.displayName(app, Uri.parse(location)) }.getOrNull() ?: f.name
            TransferRecord("$id-$i", Direction.RECEIVED, name, f.mime, f.size, from.name, now, RecordStatus.DONE, location)
        }
    }

    private fun titleOf(names: List<String>): String = if (names.size == 1) names[0] else "${names.size} files"

    private fun applyToClipboard(content: ClipContent) {
        val imageUri = clipboard.write(content)
        if (imageUri != null) notifications.clipImage(imageUri)
    }

    private fun applyClipboardFile(path: String) {
        scope.launch(Dispatchers.IO) {
            val file = File(path)
            runCatching { applyToClipboard(ClipContent.Image(file.readBytes())) }
            file.delete()
        }
    }

    private fun refreshStatus(event: String) {
        EventLog.add(event)
        _status.value = Status(
            connected = node.connectedPeers.value.map { it.name },
            paired = storage.trustStore.all().map { it.name },
            lastEvent = event,
        )
        notifications.updateStatus(statusText())
    }

    private fun isAccessibilityOn(): Boolean {
        val enabled = Settings.Secure.getString(app.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        return enabled.contains("${app.packageName}/") && enabled.contains(CopyAccessibilityService::class.java.simpleName)
    }

    private fun deviceName(): String =
        Settings.Global.getString(app.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER} ${Build.MODEL}"

    override fun close() {
        features.close()
        triggers.stop()
        connections.stop()
        clipSync.close()
        node.close()
        locks.releaseAll()
        history.saveNow()
        scope.cancel()
    }

    companion object {
        private const val MAX_CLIP_TRANSFER_BYTES = 64L * 1024 * 1024
        private const val PROGRESS_UI_MS = 500L
        private const val WAITING_SEND_MS = 2 * 60_000L

        /** The live engine while [SyncService] runs. Activities only reach it through here. */
        @Volatile
        var current: SyncEngine? = null
    }
}

/**
 * Wi-Fi and CPU locks held only while bytes are moving (ref-counted, with a hard timeout as a safety net),
 * so a transfer isn't throttled when the screen goes off, and nothing is held when idle.
 */
class TransferLocks(context: Context) {
    private val app = context.applicationContext
    private val count = AtomicInteger()
    private val wake = app.getSystemService(PowerManager::class.java)
        .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "midfielder:transfer").apply { setReferenceCounted(false) }

    @Suppress("DEPRECATION")
    private val wifi = app.getSystemService(WifiManager::class.java)
        .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "midfielder:transfer").apply { setReferenceCounted(false) }

    fun acquire() {
        if (count.incrementAndGet() == 1) {
            wake.acquire(MAX_HOLD_MS)
            wifi.acquire()
        }
    }

    fun release() {
        if (count.decrementAndGet() <= 0) releaseAll()
    }

    fun releaseAll() {
        count.set(0)
        if (wake.isHeld) wake.release()
        if (wifi.isHeld) wifi.release()
    }

    private companion object {
        const val MAX_HOLD_MS = 30 * 60_000L
    }
}
