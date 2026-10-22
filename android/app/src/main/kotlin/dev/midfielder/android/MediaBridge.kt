package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import dev.midfielder.proto.MediaAction
import dev.midfielder.proto.MediaCommand
import dev.midfielder.proto.MediaState
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream

/**
 * The phone's own music, for the PC: follows the active media session (Spotify, YouTube Music, podcasts...)
 * through notification access and sends a [MediaState] when something changes (debounced), and applies the PC's
 * [MediaCommand]s. Nothing runs until [start].
 */
class PhoneMedia(context: Context, private val send: (MediaState) -> Unit) {
    private val app = context.applicationContext
    private val sessions = app.getSystemService(MediaSessionManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private var controller: MediaController? = null
    private var started = false
    private var artKey: Any? = null
    private var artJpeg: ByteArray? = null
    private var forceArt = true
    private var last: MediaState? = null
    private var lastAt = 0L
    private val labels = HashMap<String, String>()

    private val onSessions = MediaSessionManager.OnActiveSessionsChangedListener { list -> pick(list.orEmpty()) }
    private val push = Runnable { sendNow() }
    private val callback = object : MediaController.Callback() {
        override fun onMetadataChanged(metadata: MediaMetadata?) = schedule()
        override fun onPlaybackStateChanged(state: PlaybackState?) = schedule()
        override fun onSessionDestroyed() {
            main.post { pick(activeSessions()) }
        }
    }

    fun start() {
        main.post {
            if (started || !PhoneNotificationListener.isGranted(app)) return@post
            runCatching {
                sessions.addOnActiveSessionsChangedListener(onSessions, PhoneNotificationListener.component(app), main)
                started = true
                pick(activeSessions())
            }.onFailure { EventLog.error("Can't follow the phone's music", it) }
        }
    }

    fun stop() {
        main.post {
            if (!started) return@post
            started = false
            runCatching { sessions.removeOnActiveSessionsChangedListener(onSessions) }
            controller?.unregisterCallback(callback)
            controller = null
            main.removeCallbacks(push)
        }
    }

    /** A PC (re)connected or switched music on: send the full state, art included. */
    fun resend() {
        main.post {
            forceArt = true
            last = null
            if (started) sendNow()
        }
    }

    fun command(cmd: MediaCommand) {
        main.post {
            val c = controller ?: return@post
            val t = c.transportControls
            when (cmd.action) {
                MediaAction.MEDIA_ACTION_PLAY -> t.play()
                MediaAction.MEDIA_ACTION_PAUSE -> t.pause()
                MediaAction.MEDIA_ACTION_TOGGLE -> if (c.playbackState?.state == PlaybackState.STATE_PLAYING) t.pause() else t.play()
                MediaAction.MEDIA_ACTION_NEXT -> t.skipToNext()
                MediaAction.MEDIA_ACTION_PREVIOUS -> t.skipToPrevious()
                MediaAction.MEDIA_ACTION_SEEK -> t.seekTo(cmd.seek_ms.coerceAtLeast(0))
                else -> Unit
            }
        }
    }

    private fun activeSessions(): List<MediaController> =
        runCatching { sessions.getActiveSessions(PhoneNotificationListener.component(app)) }.getOrDefault(emptyList())

    private fun pick(list: List<MediaController>) {
        val candidates = list.filter { it.packageName != app.packageName }
        val best = candidates.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING } ?: candidates.firstOrNull()
        if (best?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(callback)
            controller = best
            best?.registerCallback(callback, main)
            forceArt = true
        }
        schedule()
    }

    private fun schedule() {
        main.removeCallbacks(push)
        main.postDelayed(push, DEBOUNCE_MS)
    }

    private fun sendNow() {
        val c = controller
        val meta = c?.metadata
        val state = c?.playbackState
        if (c == null || meta == null) {
            if (last?.active != false) send(MediaState(active = false))
            last = MediaState(active = false)
            return
        }
        val playing = state?.state == PlaybackState.STATE_PLAYING
        val position = state?.let {
            val elapsed = if (playing) SystemClock.elapsedRealtime() - it.lastPositionUpdateTime else 0
            (it.position + (elapsed * it.playbackSpeed).toLong()).coerceAtLeast(0)
        } ?: 0
        // Each metadata read unparcels a new Bitmap (new generationId), so the track identifies the art; the JPEG is
        // made once per track, not on every play/pause/seek.
        val art = meta.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART) ?: meta.getBitmap(MediaMetadata.METADATA_KEY_ART)
        val key = listOf(
            meta.getString(MediaMetadata.METADATA_KEY_TITLE), meta.getString(MediaMetadata.METADATA_KEY_ARTIST),
            meta.getString(MediaMetadata.METADATA_KEY_ALBUM), art?.width, art?.height,
        )
        val newArt = key != artKey
        if (newArt) {
            artKey = key
            artJpeg = art?.let(::jpeg)
        }
        val sendArt = newArt || forceArt
        val actions = state?.actions ?: 0
        val next = MediaState(
                active = true,
                app_name = appName(c.packageName),
                title = meta.getString(MediaMetadata.METADATA_KEY_TITLE).orEmpty().take(MAX_TEXT),
                artist = (meta.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: meta.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST))
                    .orEmpty().take(MAX_TEXT),
                album = meta.getString(MediaMetadata.METADATA_KEY_ALBUM).orEmpty().take(MAX_TEXT),
                art_jpeg = if (sendArt) artJpeg?.toByteString() ?: okio.ByteString.EMPTY else okio.ByteString.EMPTY,
                art_unchanged = !sendArt,
                playing = playing,
                position_ms = position,
                duration_ms = meta.getLong(MediaMetadata.METADATA_KEY_DURATION).coerceAtLeast(0),
                updated_at_ms = System.currentTimeMillis(),
                can_next = actions and PlaybackState.ACTION_SKIP_TO_NEXT != 0L,
                can_previous = actions and PlaybackState.ACTION_SKIP_TO_PREVIOUS != 0L,
                can_seek = actions and PlaybackState.ACTION_SEEK_TO != 0L,
            )
        if (!sendArt && sameAsLast(next)) return // nothing the PC would show differently
        forceArt = false
        last = next
        lastAt = SystemClock.elapsedRealtime()
        send(next)
    }

    /** Same track, state and buttons, and the position is where the PC already expects it (within a second). */
    private fun sameAsLast(next: MediaState): Boolean {
        val prev = last ?: return false
        val expected = prev.position_ms + if (prev.playing) SystemClock.elapsedRealtime() - lastAt else 0
        return prev.copy(position_ms = 0, updated_at_ms = 0, art_jpeg = okio.ByteString.EMPTY, art_unchanged = true) ==
            next.copy(position_ms = 0, updated_at_ms = 0, art_jpeg = okio.ByteString.EMPTY, art_unchanged = true) &&
            kotlin.math.abs(next.position_ms - expected) < 1_000
    }

    private fun appName(pkg: String): String = labels.getOrPut(pkg) {
        runCatching {
            val pm = app.packageManager
            pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()
        }.getOrDefault(pkg)
    }

    private companion object {
        const val DEBOUNCE_MS = 250L
        const val MAX_TEXT = 200
    }
}

/**
 * The PC's music on the phone: a media session (so it shows in Android's media controls and on the lock screen)
 * mirroring the PC's last [MediaState]; buttons go back to the PC as [MediaCommand]s.
 */
class PcMedia(context: Context, private val notifications: Notifications, private val command: (MediaCommand) -> Unit) {
    private val app = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private var session: MediaSession? = null
    private var art: Bitmap? = null

    fun show(state: MediaState) {
        main.post {
            if (!state.active) {
                clearNow()
                return@post
            }
            val s = session ?: MediaSession(app, "Midfielder PC").also {
                it.setCallback(object : MediaSession.Callback() {
                    override fun onPlay() = command(MediaCommand(action = MediaAction.MEDIA_ACTION_PLAY))
                    override fun onPause() = command(MediaCommand(action = MediaAction.MEDIA_ACTION_PAUSE))
                    override fun onSkipToNext() = command(MediaCommand(action = MediaAction.MEDIA_ACTION_NEXT))
                    override fun onSkipToPrevious() = command(MediaCommand(action = MediaAction.MEDIA_ACTION_PREVIOUS))
                    override fun onSeekTo(pos: Long) = command(MediaCommand(action = MediaAction.MEDIA_ACTION_SEEK, seek_ms = pos))
                })
                session = it
            }
            if (!state.art_unchanged) {
                art = state.art_jpeg.takeIf { it.size > 0 }?.toByteArray()?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
            }
            s.setMetadata(
                MediaMetadata.Builder()
                    .putString(MediaMetadata.METADATA_KEY_TITLE, state.title)
                    .putString(MediaMetadata.METADATA_KEY_ARTIST, state.artist)
                    .putString(MediaMetadata.METADATA_KEY_ALBUM, state.album)
                    .putLong(MediaMetadata.METADATA_KEY_DURATION, state.duration_ms)
                    .apply { art?.let { putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, it) } }
                    .build(),
            )
            var actions = PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE
            if (state.can_next) actions = actions or PlaybackState.ACTION_SKIP_TO_NEXT
            if (state.can_previous) actions = actions or PlaybackState.ACTION_SKIP_TO_PREVIOUS
            if (state.can_seek) actions = actions or PlaybackState.ACTION_SEEK_TO
            s.setPlaybackState(
                PlaybackState.Builder()
                    .setActions(actions)
                    .setState(if (state.playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED, state.position_ms, if (state.playing) 1f else 0f)
                    .build(),
            )
            s.isActive = true
            notifications.pcMedia(s.sessionToken, state, art)
        }
    }

    fun clear() {
        main.post { clearNow() }
    }

    private fun clearNow() {
        notifications.cancelPcMedia()
        session?.run {
            isActive = false
            release()
        }
        session = null
        art = null
    }
}

/** The previous / play-pause / next buttons on the "Playing on your PC" notification. */
class PcMediaReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = MediaAction.fromValue(intent.getIntExtra(EXTRA_ACTION, 0)) ?: return
        val features = SyncEngine.current?.features
        if (features != null) features.sendMediaCommand(MediaCommand(action = action)) else Notifications(context).cancelPcMedia()
    }

    companion object {
        const val EXTRA_ACTION = "action"
    }
}

/** Ring notification's Stop button. */
class RingStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val ringer = SyncEngine.current?.features?.ringer
        if (ringer != null) ringer.stop() else Notifications(context).cancelRinging()
    }
}

private fun jpeg(src: Bitmap): ByteArray? {
    val scale = minOf(1f, ART_PX.toFloat() / maxOf(src.width, src.height))
    val bmp = if (scale < 1f) Bitmap.createScaledBitmap(src, (src.width * scale).toInt(), (src.height * scale).toInt(), true) else src
    return ByteArrayOutputStream().use { out ->
        bmp.compress(Bitmap.CompressFormat.JPEG, 80, out)
        if (bmp !== src) bmp.recycle()
        out.toByteArray()
    }.takeIf { it.size <= MAX_ART_BYTES }
}

private const val ART_PX = 300
private const val MAX_ART_BYTES = 64 * 1024
