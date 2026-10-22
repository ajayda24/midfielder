package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.net.Uri
import android.os.Handler
import android.os.Looper

/**
 * "Find my phone": plays the alarm sound on the alarm stream at full volume (so it rings on silent and through
 * Do Not Disturb when alarms are allowed), shows a Stop notification and stops on its own after [MAX_MS], or as
 * soon as the screen is turned on (the phone was found). The user's alarm volume is put back afterwards, also
 * after a crash mid-ring (it's saved until restored).
 */
class Ringer(context: Context, private val notifications: Notifications) {
    private val app = context.applicationContext
    private val audio = app.getSystemService(AudioManager::class.java)
    private val prefs = app.getSharedPreferences("ringer", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var screenOn: BroadcastReceiver? = null
    private val autoStop = Runnable { stop() }

    val isRinging: Boolean get() = player != null

    fun start() {
        main.post {
            if (player != null) return@post
            raiseVolume()
            player = sounds().firstNotNullOfOrNull { uri -> play(uri) }
            if (player == null) {
                EventLog.add("Couldn't play any ring sound")
                restoreVolume()
                return@post
            }
            notifications.ringing()
            main.postDelayed(autoStop, MAX_MS)
            watchScreen()
            EventLog.add("Ringing: the PC is looking for this phone")
        }
    }

    fun stop() {
        main.post {
            main.removeCallbacks(autoStop)
            screenOn?.let { runCatching { app.unregisterReceiver(it) } }
            screenOn = null
            notifications.cancelRinging() // also when nothing plays: a leftover notification must go
            val p = player ?: return@post
            player = null
            runCatching {
                p.stop()
                p.release()
            }
            restoreVolume()
        }
    }

    /** A previous run died mid-ring: put the user's alarm volume back. */
    fun restoreAfterCrash() {
        if (prefs.contains(SAVED_VOLUME)) restoreVolume()
    }

    /**
     * The alarm sound through the settings provider first (it works without media permission even when the user
     * picked a song as their alarm), then the actual file, then the ringtone.
     */
    private fun sounds(): List<Uri> = listOfNotNull(
        RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM),
        runCatching { RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_ALARM) }.getOrNull(),
        RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE),
    ).distinct()

    private fun play(uri: Uri): MediaPlayer? = runCatching {
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
    }.onFailure { EventLog.error("Ring sound $uri didn't play", it) }.getOrNull()

    /** Turning the screen on means the phone was found. */
    private fun watchScreen() {
        val r = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = stop()
        }
        screenOn = r
        app.registerReceiver(r, IntentFilter(Intent.ACTION_SCREEN_ON))
    }

    private fun raiseVolume() {
        runCatching {
            if (!prefs.contains(SAVED_VOLUME)) prefs.edit().putInt(SAVED_VOLUME, audio.getStreamVolume(AudioManager.STREAM_ALARM)).commit()
            audio.setStreamVolume(AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
        }
    }

    private fun restoreVolume() {
        val saved = prefs.getInt(SAVED_VOLUME, -1)
        if (saved >= 0) runCatching { audio.setStreamVolume(AudioManager.STREAM_ALARM, saved, 0) }
        prefs.edit().remove(SAVED_VOLUME).apply()
    }

    private companion object {
        const val MAX_MS = 30_000L
        const val SAVED_VOLUME = "saved_alarm_volume"
    }
}
