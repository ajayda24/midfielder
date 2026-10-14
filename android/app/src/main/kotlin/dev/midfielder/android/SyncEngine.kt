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
import dev.midfielder.core.clip.ClipSync
import dev.midfielder.core.clip.ClipboardHistory
import dev.midfielder.core.crypto.PairingInvite
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
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val storage = AppStorage(app)
    private val identity = storage.loadOrCreateIdentity()
    private val notifications = Notifications(app)
    private val clipboard = ClipboardBridge(app)
    private val locks = TransferLocks(app)
    private val clipTransfers = ConcurrentHashMap.newKeySet<String>()
    private val lastProgressUi = ConcurrentHashMap<String, Long>()
    private val _status = MutableStateFlow(Status())
    val status: StateFlow<Status> get() = _status

    @Volatile
    var uiInForeground = false

    private val events: NodeEvents = object : NodeEvents {
        override fun onPaired(device: PairedDevice) = refreshStatus("Paired with ${device.name}")

        override fun onPeerConnected(session: PeerSession) {
            clipSync.catchUpItem()?.let { session.trySend(Envelope(clipboard = it)) }
            refreshStatus("Connected to ${session.peer.name}")
        }

        override fun onPeerDisconnected(device: PairedDevice, cause: Throwable?) {
            refreshStatus("Disconnected from ${device.name}")
            connections.kick("disconnected")
        }

        override suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {
            clipSync.onRemote(item)
        }

        override suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink>? {
            if (offer.purpose == TransferPurpose.TRANSFER_PURPOSE_CLIPBOARD) {
                if (offer.files.size != 1 || offer.files[0].size > MAX_CLIP_TRANSFER_BYTES) return null
                clipTransfers += offer.transfer_id
                return listOf(DirectoryFileSink(File(app.cacheDir, "clip-in").toPath(), offer.transfer_id, "clipboard.png"))
            }
            if (!storage.acceptFiles) return null
            locks.acquire()
            return offer.files.mapIndexed { i, f -> MediaStoreFileSink(app, "${offer.transfer_id}-$i", f.name, f.mime) }
        }

        override fun onTransferProgress(transferId: String, transferred: Long, total: Long) {
            if (transferId in clipTransfers) return
            val now = SystemClock.elapsedRealtime()
            if (transferred < total && now - (lastProgressUi[transferId] ?: 0L) < PROGRESS_UI_MS) return
            lastProgressUi[transferId] = now
            notifications.progress(transferId, "Transferring…", transferred, total)
        }

        override fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {
            lastProgressUi.remove(transferId)
            if (clipTransfers.remove(transferId)) {
                if (result is TransferResult.Completed) applyClipboardFile(result.locations.single())
                return
            }
            locks.release()
            when (result) {
                is TransferResult.Completed ->
                    notifications.finished(transferId, "Received from ${from.name}", "${result.locations.size} file(s) in Download/Midfielder")
                is TransferResult.Failed -> notifications.finished(transferId, "Transfer from ${from.name} failed", result.reason)
                TransferResult.Cancelled -> notifications.cancel(transferId)
            }
        }
    }

    private val node: MidfielderNode = MidfielderNode(
        identity, deviceName(), DeviceKind.DEVICE_KIND_ANDROID, storage.trustStore, events,
        // elapsedRealtime keeps counting in deep sleep, so heartbeat math stays right after the phone naps.
        NodeConfig(clock = SystemClock::elapsedRealtime),
    )

    val clipSync: ClipSync = ClipSync(
        identity.deviceId, scope,
        broadcast = { node.broadcastClipboard(it) },
        apply = { content, _ -> clipboard.write(content) },
        history = ClipboardHistory(maxItems = 50, maxTotalBytes = 8L * 1024 * 1024),
    ).apply { enabled = storage.syncClipboard }

    private val connections: ConnectionManager = ConnectionManager(app, node, storage.trustStore, scope)
    private val triggers: CaptureTriggers = CaptureTriggers(app, scope) { requestClipboardRead() }

    val captureMode: String
        get() = when {
            triggers.hasReadLogs -> "Automatic (ADB permission)"
            isAccessibilityOn() -> "Automatic (accessibility)"
            else -> "Tap \"Send clipboard\" (or enable automatic mode)"
        }

    fun start() {
        val power = app.getSystemService(PowerManager::class.java)
        connections.onScreenChanged = { on ->
            triggers.onScreen(on)
            if (on) for (d in node.connectedPeers.value) node.session(d.fingerprint)?.probe()
        }
        connections.start()
        triggers.start(power.isInteractive)
        refreshStatus("Started")
    }

    fun statusText(): String {
        val s = _status.value
        return when {
            s.connected.isNotEmpty() -> "Connected to ${s.connected.joinToString()}"
            s.paired.isNotEmpty() -> "Waiting for ${s.paired.joinToString()}"
            else -> "Not paired yet: open Midfielder to pair with your PC"
        }
    }

    suspend fun pair(invite: PairingInvite): PairedDevice {
        val session = node.pair(invite)
        storage.trustStore.put(session.peer.copy(lastAddress = session.remote.toString()))
        refreshStatus("Paired with ${session.peer.name}")
        return session.peer
    }

    fun unpairAll() {
        for (d in storage.trustStore.all()) node.unpair(d.fingerprint)
        refreshStatus("Forgot paired PCs")
    }

    /** A trigger fired: read the clipboard now (directly if our UI is in front, else via the reader activity). */
    fun requestClipboardRead() {
        if (!clipSync.enabled || clipboard.isSuppressed || storage.trustStore.all().isEmpty()) return
        if (uiInForeground) {
            clipboard.read()?.let(::onLocalClipboard)
            return
        }
        val canStartFromBackground = Settings.canDrawOverlays(app) || isAccessibilityOn()
        if (!canStartFromBackground) return
        runCatching {
            app.startActivity(
                Intent(app, ClipboardReaderActivity::class.java).addFlags(
                    Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION or Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS,
                ),
            )
        }
    }

    /** Called by the reader activity, the share sheet and the UI with fresh clipboard content. */
    fun onLocalClipboard(content: ClipContent) {
        if (clipSync.isInlineSize(content)) {
            if (clipSync.onLocalChange(content) && node.connectedPeers.value.isEmpty()) connections.kick("clipboard")
            return
        }
        if (content is ClipContent.Image) {
            val source = BytesFileSource("clipboard.png", content.png, "image/png")
            sendToPcs(listOf(source), TransferPurpose.TRANSFER_PURPOSE_CLIPBOARD)
        }
    }

    fun sendUris(uris: List<Uri>) {
        val sources = uris.mapNotNull { runCatching { UriFileSource(app, it) }.getOrNull() }
        if (sources.isNotEmpty()) sendToPcs(sources, TransferPurpose.TRANSFER_PURPOSE_FILE)
    }

    private fun sendToPcs(sources: List<FileSource>, purpose: TransferPurpose) {
        val target = node.connectedPeers.value.firstOrNull()
        if (target == null) {
            refreshStatus("PC not connected")
            connections.kick("send")
            return
        }
        scope.launch {
            locks.acquire()
            try {
                val result = node.sendFiles(target.fingerprint, sources, purpose)
                if (purpose == TransferPurpose.TRANSFER_PURPOSE_FILE) {
                    val id = "send-${sources.hashCode()}"
                    when (result) {
                        is TransferResult.Completed -> notifications.finished(id, "Sent to ${target.name}", "${sources.size} file(s)")
                        is TransferResult.Failed -> notifications.finished(id, "Not sent", result.reason)
                        TransferResult.Cancelled -> Unit
                    }
                }
            } catch (e: Exception) {
                refreshStatus("Send failed: ${e.message}")
            } finally {
                locks.release()
            }
        }
    }

    private fun applyClipboardFile(path: String) {
        scope.launch(Dispatchers.IO) {
            val file = File(path)
            runCatching { clipboard.write(ClipContent.Image(file.readBytes())) }
            file.delete()
        }
    }

    private fun refreshStatus(event: String) {
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
        triggers.stop()
        connections.stop()
        clipSync.close()
        node.close()
        locks.releaseAll()
        scope.cancel()
    }

    companion object {
        private const val MAX_CLIP_TRANSFER_BYTES = 64L * 1024 * 1024
        private const val PROGRESS_UI_MS = 500L

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
