package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.net.wifi.WifiManager
import android.os.PowerManager
import dev.midfielder.core.crypto.Fingerprint
import dev.midfielder.core.feature.AudioFrames
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.proto.AudioOffer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The PC's sound on this phone: opens the stream the PC offered and plays its 20 ms PCM chunks through an
 * [AudioTrack]. Playback starts once a few chunks are in, and at most [MAX_QUEUED_MS] is ever queued (late audio is
 * dropped), so the delay stays small. Wi-Fi and CPU locks are held only while it plays.
 *
 * Behaves like a good media app: takes audio focus (other music pauses; a call or another player taking over stops
 * it), stops when headphones are unplugged instead of switching to the loudspeaker, and stops by itself after
 * [SILENCE_STOP_MS] of silence (the PC keeps sending silence while nothing plays), telling the PC each time.
 */
class PcAudioPlayer(
    context: Context,
    private val node: MidfielderNode,
    private val notifications: Notifications,
    private val onPlaying: (Boolean) -> Unit,
    /** Tells the PC to stop capturing (the phone stopped by itself or failed). */
    private val tellPc: (streamId: String, reason: String) -> Unit,
) {
    private val app = context.applicationContext
    private val audio = app.getSystemService(android.media.AudioManager::class.java)
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.IO + kotlinx.coroutines.CoroutineExceptionHandler { _, e -> EventLog.error("PC audio", e) },
    )
    private var job: Job? = null

    /** The stream playing now, if any. */
    @Volatile
    var streamId: String? = null
        private set

    val isPlaying: Boolean get() = streamId != null

    @Synchronized
    fun play(peer: Fingerprint, offer: AudioOffer) {
        stop()
        val id = offer.stream_id
        val rate = offer.sample_rate.takeIf { it in 8_000..192_000 } ?: 48_000
        val channels = if (offer.channels == 1) 1 else 2
        streamId = id
        onPlaying(true)
        notifications.pcAudio()
        job = scope.launch {
            var locks: Locks? = null
            var track: AudioTrack? = null
            var focus: android.media.AudioFocusRequest? = null
            var noisy: android.content.BroadcastReceiver? = null
            var reason: String? = null
            try {
                locks = Locks(app)
                val t = buildTrack(rate, channels)
                track = t
                focus = requestFocus(id) ?: run {
                    reason = "another app is using the audio"
                    return@launch
                }
                noisy = watchHeadphones(id)
                EventLog.add("Playing the PC's audio on this phone")
                node.openStream(peer, id) { input -> reason = pump(input, t, rate, channels) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (isActive) {
                    EventLog.error("PC audio stopped", e)
                    reason = "failed on the phone"
                }
            } finally {
                track?.let {
                    runCatching { it.stop() }
                    it.release()
                }
                focus?.let { audio?.abandonAudioFocusRequest(it) }
                noisy?.let { runCatching { app.unregisterReceiver(it) } }
                locks?.release()
                reason?.let {
                    EventLog.add("PC audio stopped: $it")
                    tellPc(id, it)
                }
                ended(id)
            }
        }
    }

    /** Other players pause while the PC's sound plays here; losing focus (a call, another player) stops it. */
    private fun requestFocus(id: String): android.media.AudioFocusRequest? {
        val am = audio ?: return null
        val request = android.media.AudioFocusRequest.Builder(android.media.AudioManager.AUDIOFOCUS_GAIN)
            .setAudioAttributes(attributes())
            .setOnAudioFocusChangeListener { change ->
                if (change == android.media.AudioManager.AUDIOFOCUS_LOSS || change == android.media.AudioManager.AUDIOFOCUS_LOSS_TRANSIENT) {
                    stopAndTell(id, "another app took over the audio")
                }
            }
            .build()
        return request.takeIf { am.requestAudioFocus(it) == android.media.AudioManager.AUDIOFOCUS_REQUEST_GRANTED }
    }

    /** Headphones unplugged (or Bluetooth headset gone): stop rather than suddenly play out loud. */
    private fun watchHeadphones(id: String): android.content.BroadcastReceiver {
        val r = object : android.content.BroadcastReceiver() {
            override fun onReceive(context: Context, intent: android.content.Intent) = stopAndTell(id, "headphones disconnected")
        }
        app.registerReceiver(r, android.content.IntentFilter(android.media.AudioManager.ACTION_AUDIO_BECOMING_NOISY))
        return r
    }

    private fun stopAndTell(id: String, reason: String) {
        if (streamId != id) return
        EventLog.add("PC audio stopped: $reason")
        tellPc(id, reason)
        stop(id)
    }

    /** Stops whatever plays (or only [id] when given). */
    @Synchronized
    fun stop(id: String? = null) {
        if (id != null && id != streamId) return
        job?.cancel()
        job = null
        streamId?.let(::ended)
    }

    fun close() {
        stop()
        scope.cancel()
    }

    @Synchronized
    private fun ended(id: String) {
        if (streamId != id) return
        streamId = null
        notifications.cancelPcAudio()
        onPlaying(false)
    }

    /** Plays until the stream ends; returns why it stopped by itself (null when the PC ended it). */
    private fun pump(input: java.io.InputStream, track: AudioTrack, rate: Int, channels: Int): String? {
        val frameBytes = channels * 2
        val maxQueuedFrames = rate / 1000 * MAX_QUEUED_MS
        val silentLimit = SILENCE_STOP_MS / AudioFrames.CHUNK_MS
        var written = 0L
        var chunks = 0
        var silent = 0L
        var started = false
        var lastHead = 0L
        var wraps = 0L
        while (true) {
            val chunk = AudioFrames.read(input) ?: return null
            val pcm = chunk.pcm.toByteArray()
            silent = if (pcm.all { it == 0.toByte() }) silent + 1 else 0
            if (silent >= silentLimit) return "nothing played for ${SILENCE_STOP_MS / 60_000} minutes"
            if (started) {
                // The PC's clock runs a little faster than ours, or Wi-Fi delivered a burst: drop rather than lag.
                val head32 = track.playbackHeadPosition.toLong() and 0xFFFFFFFFL // wraps after ~24 h at 48 kHz
                if (head32 < lastHead) wraps++
                lastHead = head32
                val queued = written - (head32 + (wraps shl 32))
                if (queued > maxQueuedFrames) continue
            }
            val n = track.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING)
            if (n > 0) written += n / frameBytes
            if (!started && ++chunks >= PREBUFFER_CHUNKS) {
                track.play()
                started = true
            }
        }
    }

    private fun attributes(): AudioAttributes =
        AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build()

    private fun buildTrack(rate: Int, channels: Int): AudioTrack {
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        val wanted = AudioFrames.chunkBytes(rate, channels) * (BUFFER_MS / AudioFrames.CHUNK_MS)
        return AudioTrack.Builder()
            .setAudioAttributes(attributes())
            .setAudioFormat(
                AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate).setChannelMask(mask).build(),
            )
            .setBufferSizeInBytes(maxOf(min, wanted))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
    }

    /** Keeps Wi-Fi awake and responsive, and the CPU running, while audio streams in. */
    private class Locks(app: Context) {
        private val wifi = app.getSystemService(WifiManager::class.java)
            ?.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "midfielder:audio")
            ?.apply {
                setReferenceCounted(false)
                acquire()
            }
        private val cpu = app.getSystemService(PowerManager::class.java)
            ?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "midfielder:audio")
            ?.apply {
                setReferenceCounted(false)
                acquire(MAX_LOCK_MS)
            }

        fun release() {
            runCatching { wifi?.release() }
            runCatching { cpu?.release() }
        }
    }

    private companion object {
        const val PREBUFFER_CHUNKS = 5 // 100 ms
        const val BUFFER_MS = 300
        const val MAX_QUEUED_MS = 200
        const val MAX_LOCK_MS = 4 * 60 * 60_000L
        const val SILENCE_STOP_MS = 5 * 60_000L
    }
}

/** Stop on the "PC audio on this phone" notification. */
class PcAudioStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val features = SyncEngine.current?.features
        if (features != null) features.stopPcAudio() else Notifications(context).cancelPcAudio() // left over from a dead process
    }
}
