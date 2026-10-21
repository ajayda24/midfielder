package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import dev.midfielder.core.feature.BatteryAlerts
import dev.midfielder.core.feature.Features
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.PeerSession
import dev.midfielder.proto.AudioRequest
import dev.midfielder.proto.AudioRoute
import dev.midfielder.proto.AudioStop
import dev.midfielder.proto.BatteryStatus
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.MediaCommand
import dev.midfielder.proto.MediaState
import dev.midfielder.proto.NotificationRemoved
import dev.midfielder.proto.RingRequest
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The optional features on the phone: notifications and music on the PC, battery sharing, find my phone, audio
 * between the devices, calls, the auto-lock beacon. Every one
 * has its own switch in [AppStorage]; [apply] starts what is on and stops what is off (receivers unregistered,
 * listener component disabled, nothing sent), and tells the PC which features are on.
 */
class PhoneFeatures(
    context: Context,
    private val node: MidfielderNode,
    private val storage: AppStorage,
    private val notifs: Notifications,
) {
    /** The connected PC as the UI shows it. */
    data class Pc(
        val name: String? = null,
        val battery: BatteryStatus? = null,
        val capabilities: Set<String> = emptySet(),
        /** The PC's sound is playing on this phone. */
        val audioHere: Boolean = false,
        /** The PC has sent its auto-lock key, so the beacon can run. */
        val presenceReady: Boolean = false,
    ) {
        val canRing: Boolean get() = Features.RING in capabilities
        val canAudio: Boolean get() = Features.AUDIO in capabilities
    }

    private val app = context.applicationContext
    private val _pc = MutableStateFlow(Pc())
    val pc: StateFlow<Pc> get() = _pc

    val ringer = Ringer(app, notifs)
    val notifications = PhoneNotifications(app, storage) { posted, removedKey ->
        val env = if (posted != null) Envelope(notification_posted = posted) else Envelope(notification_removed = NotificationRemoved(key = removedKey!!))
        node.sendFeature(Features.NOTIFY, env)
    }
    private val phoneMedia = PhoneMedia(app) { node.sendFeature(Features.MEDIA, Envelope(media_state = it)) }
    private val pcMedia = PcMedia(app, notifs) { sendMediaCommand(it) }
    private val pcAudio = PcAudioPlayer(app, node, notifs) { playing -> _pc.value = _pc.value.copy(audioHere = playing) }
    val calls = CallBridge(app, storage) { node.sendFeature(Features.CALL, Envelope(call_state = it)) }
    val beacon = PresenceBeacon(app)
    private val alerts = BatteryAlerts()
    private var batteryReceiver: BroadcastReceiver? = null

    @Volatile
    private var lastBattery: BatteryStatus? = null
    private var phoneMediaOn = false

    /** Applies the switches. Call at start and whenever one changes. */
    fun apply() {
        PhoneNotificationListener.setEnabled(app, storage.featNotify || phoneMusicOn())
        if (!storage.featNotify) notifications.clear()
        if (storage.featBattery) startBattery() else stopBattery()
        if (phoneMusicOn()) {
            phoneMedia.start()
            phoneMediaOn = true
        } else if (phoneMediaOn) {
            phoneMedia.stop()
            phoneMediaOn = false
            node.sendFeature(Features.MEDIA, Envelope(media_state = MediaState(active = false)))
        }
        if (!(storage.featMedia && storage.mediaPcToPhone)) pcMedia.clear()
        if (!storage.featRing) ringer.stop()
        if (!(storage.featAudio && storage.audioPcToPhone)) stopPcAudio()
        if (storage.featCalls) {
            calls.start()
            calls.resend() // options (answer, SMS) may have changed
        } else {
            calls.stop()
        }
        val key = storage.presenceKey
        if (storage.featPresence && key != null) beacon.start(key) else beacon.stop()
        alerts.fullAt = storage.batteryFullAt
        node.setFeatures(enabled())
        refreshPc()
    }

    fun enabled(): Set<String> = buildSet {
        if (storage.featNotify) add(Features.NOTIFY)
        if (storage.featBattery) add(Features.BATTERY)
        if (storage.featRing) add(Features.RING)
        if (storage.featMedia) add(Features.MEDIA)
        if (storage.featAudio) add(Features.AUDIO)
        if (storage.featCalls) add(Features.CALL)
        if (storage.featPresence) add(Features.PRESENCE)
    }

    /** True when a switch is on but Android hasn't granted notification access yet. */
    val needsNotificationAccess: Boolean
        get() = (storage.featNotify || phoneMusicOn()) && !PhoneNotificationListener.isGranted(app)

    private fun phoneMusicOn() = storage.featMedia && storage.mediaPhoneToPc

    /** Notification access was granted or revoked (the listener connected or disconnected). */
    fun onListenerChanged() {
        if (phoneMusicOn()) phoneMedia.start()
    }

    fun onPeerConnected(session: PeerSession) {
        refreshPc()
        if (session.has(Features.BATTERY)) lastBattery?.let { session.trySend(Envelope(battery_status = it)) }
        if (session.has(Features.MEDIA)) phoneMedia.resend()
        if (session.has(Features.CALL)) calls.resend()
    }

    fun onPeerDisconnected() {
        refreshPc()
        if (node.connectedPeers.value.isEmpty()) {
            pcMedia.clear()
            pcAudio.stop()
        }
    }

    fun onMessage(from: PeerSession, env: Envelope) {
        // Locals: the generated properties live in :core, so Kotlin won't smart-cast them.
        val action = env.notification_action
        val battery = env.battery_status
        val ring = env.ring_request
        val mediaState = env.media_state
        val mediaCommand = env.media_command
        val audioOffer = env.audio_offer
        val audioStop = env.audio_stop
        val callCommand = env.call_command
        val presenceKey = env.presence_key
        when {
            env.features_changed != null -> {
                refreshPc()
                if (from.has(Features.BATTERY)) lastBattery?.let { from.trySend(Envelope(battery_status = it)) }
                if (from.has(Features.MEDIA)) phoneMedia.resend() else pcMedia.clear()
            }
            action != null -> if (storage.featNotify) notifications.act(action)
            battery != null -> if (storage.featBattery) onPcBattery(battery)
            ring != null -> if (storage.featRing) {
                if (ring.start) ringer.start() else ringer.stop()
            }
            mediaState != null -> if (storage.featMedia && storage.mediaPcToPhone) pcMedia.show(mediaState)
            mediaCommand != null -> if (phoneMusicOn()) phoneMedia.command(mediaCommand)
            audioOffer != null -> if (storage.featAudio && storage.audioPcToPhone) {
                pcAudio.play(from.peer.fingerprint, audioOffer)
            } else {
                from.trySend(Envelope(audio_stop = AudioStop(stream_id = audioOffer.stream_id, reason = "off on the phone")))
            }
            audioStop != null -> pcAudio.stop(audioStop.stream_id)
            callCommand != null -> if (storage.featCalls) calls.command(callCommand)
            presenceKey != null -> if (storage.featPresence) onPresenceKey(presenceKey.key.toByteArray())
        }
    }

    /** "Ring PC" on the phone; false if no connected PC can ring. */
    fun ringPc(start: Boolean = true): Boolean =
        storage.featRing && node.sendFeature(Features.RING, Envelope(ring_request = RingRequest(start = start))) > 0

    /** Asks the PC to start or stop an audio route; false if no connected PC has Audio on. */
    fun requestAudio(route: AudioRoute, start: Boolean): Boolean {
        if (!storage.featAudio) return false
        return node.sendFeature(Features.AUDIO, Envelope(audio_request = AudioRequest(route = route, start = start))) > 0
    }

    /** Stops the PC's sound here and tells the PC, so it stops capturing. */
    fun stopPcAudio() {
        val id = pcAudio.streamId ?: return
        node.sendFeature(Features.AUDIO, Envelope(audio_stop = AudioStop(stream_id = id, reason = "stopped on the phone")))
        pcAudio.stop(id)
    }

    private fun onPresenceKey(key: ByteArray) {
        if (key.size != 32) return
        if (!key.contentEquals(storage.presenceKey)) {
            storage.presenceKey = key
            EventLog.add("Auto-lock: got the key from your PC")
        }
        beacon.start(key)
        refreshPc()
    }

    fun sendMediaCommand(cmd: MediaCommand) {
        if (storage.featMedia) node.sendFeature(Features.MEDIA, Envelope(media_command = cmd))
    }

    fun close() {
        stopBattery()
        phoneMedia.stop()
        pcMedia.clear()
        ringer.stop()
        pcAudio.close()
        calls.stop()
        beacon.stop()
    }

    private fun onPcBattery(b: BatteryStatus) {
        _pc.value = _pc.value.copy(battery = b)
        if (!storage.batteryAlerts) return
        val pc = _pc.value.name ?: "Your PC"
        when (alerts.update(b.level, b.charging)) {
            BatteryAlerts.Alert.FULL -> notifs.batteryAlert("$pc is charged to ${b.level}%", "Unplug it to keep the battery healthy")
            BatteryAlerts.Alert.LOW -> notifs.batteryAlert("$pc battery is low: ${b.level}%", "Plug it in soon")
            null -> Unit
        }
    }

    private fun refreshPc() {
        val peer = node.connectedPeers.value.firstOrNull()
        val caps = peer?.let { node.session(it.fingerprint)?.capabilities }.orEmpty()
        val battery = if (peer != null && Features.BATTERY in caps && storage.featBattery) _pc.value.battery else null
        _pc.value = Pc(peer?.name, battery, caps, audioHere = pcAudio.isPlaying, presenceReady = storage.presenceKey != null)
    }

    // ---- Battery -------------------------------------------------------------------------------------

    private fun startBattery() {
        if (batteryReceiver != null) return
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = onBatteryIntent(intent)
        }
        batteryReceiver = receiver
        // The sticky intent comes back right away with the current level.
        val filter = IntentFilter(Intent.ACTION_BATTERY_CHANGED)
        val sticky = if (android.os.Build.VERSION.SDK_INT >= 33) {
            app.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            app.registerReceiver(receiver, filter)
        }
        sticky?.let(::onBatteryIntent)
    }

    private fun stopBattery() {
        batteryReceiver?.let { runCatching { app.unregisterReceiver(it) } }
        batteryReceiver = null
        lastBattery = null
        _pc.value = _pc.value.copy(battery = null)
    }

    private fun onBatteryIntent(intent: Intent) {
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        if (level < 0 || scale <= 0) return
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val b = BatteryStatus(
            level = (level * 100 / scale).coerceIn(0, 100),
            charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL,
            plugged = intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0,
        )
        if (b == lastBattery) return // ACTION_BATTERY_CHANGED also fires for temperature and voltage
        lastBattery = b
        node.sendFeature(Features.BATTERY, Envelope(battery_status = b))
    }
}
