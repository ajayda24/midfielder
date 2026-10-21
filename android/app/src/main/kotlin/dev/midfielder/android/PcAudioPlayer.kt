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
 */
class PcAudioPlayer(
    context: Context,
    private val node: MidfielderNode,
    private val notifications: Notifications,
    private val onPlaying: (Boolean) -> Unit,
) {
    private val app = context.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
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
            val locks = Locks(app)
            val track = buildTrack(rate, channels)
            try {
                EventLog.add("Playing the PC's audio on this phone")
                node.openStream(peer, id) { input -> pump(input, track, rate, channels) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (isActive) EventLog.error("PC audio stopped", e) // else we closed it
            } finally {
                runCatching { track.stop() }
                track.release()
                locks.release()
                ended(id)
            }
        }
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

    private fun pump(input: java.io.InputStream, track: AudioTrack, rate: Int, channels: Int) {
        val frameBytes = channels * 2
        val maxQueuedFrames = rate / 1000 * MAX_QUEUED_MS
        var written = 0L
        var chunks = 0
        var started = false
        while (true) {
            val chunk = AudioFrames.read(input) ?: return
            val pcm = chunk.pcm.toByteArray()
            if (started) {
                // The PC's clock runs a little faster than ours, or Wi-Fi delivered a burst: drop rather than lag.
                val queued = written - (track.playbackHeadPosition.toLong() and 0xFFFFFFFFL)
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

    private fun buildTrack(rate: Int, channels: Int): AudioTrack {
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        val wanted = AudioFrames.chunkBytes(rate, channels) * (BUFFER_MS / AudioFrames.CHUNK_MS)
        return AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build(),
            )
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
    }
}

/** Stop on the "PC audio on this phone" notification. */
class PcAudioStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        SyncEngine.current?.features?.stopPcAudio()
    }
}
