package dev.midfielder.android

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper

/**
 * "Find my phone": plays the alarm sound on the alarm stream at full volume (so it rings on silent and through
 * Do Not Disturb when alarms are allowed), shows a Stop notification and stops on its own after [MAX_MS].
 * The user's alarm volume is put back afterwards.
 */
class Ringer(context: Context, private val notifications: Notifications) {
    private val app = context.applicationContext
    private val audio = app.getSystemService(AudioManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var savedVolume = -1
    private val autoStop = Runnable { stop() }

    val isRinging: Boolean get() = player != null

    fun start() {
        main.post {
            if (player != null) return@post
            val uri = RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_ALARM)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
                ?: return@post
            savedVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
            audio.setStreamVolume(AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
            player = runCatching {
                MediaPlayer().apply {
                    setAudioAttributes(
                        AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM)
                            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build(),
                    )
                    setDataSource(app, uri)
                    isLooping = true
                    prepare()
                    start()
                }
            }.onFailure { EventLog.error("Couldn't play the ring sound", it) }.getOrNull()
            if (player == null) {
                restoreVolume()
                return@post
            }
            notifications.ringing()
            main.postDelayed(autoStop, MAX_MS)
            EventLog.add("Ringing: the PC is looking for this phone")
        }
    }

    fun stop() {
        main.post {
            main.removeCallbacks(autoStop)
            val p = player ?: return@post
            player = null
            runCatching {
                p.stop()
                p.release()
            }
            restoreVolume()
            notifications.cancelRinging()
        }
    }

    private fun restoreVolume() {
        if (savedVolume >= 0) audio.setStreamVolume(AudioManager.STREAM_ALARM, savedVolume, 0)
        savedVolume = -1
    }

    private companion object {
        const val MAX_MS = 30_000L
    }
}
