package dev.midfielder.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build

/** All notifications in one place. The always-on one is IMPORTANCE_MIN: no sound, no status-bar icon. */
class Notifications(context: Context) {
    private val app = context.applicationContext
    private val nm = app.getSystemService(NotificationManager::class.java)

    init {
        nm.createNotificationChannels(
            listOf(
                NotificationChannel(STATUS, "Connection status", NotificationManager.IMPORTANCE_MIN).apply {
                    setShowBadge(false)
                },
                NotificationChannel(TRANSFERS, "File transfers", NotificationManager.IMPORTANCE_LOW),
                NotificationChannel(EVENTS, "Received files", NotificationManager.IMPORTANCE_DEFAULT),
                NotificationChannel(CLIPS, "Images copied from PC", NotificationManager.IMPORTANCE_LOW),
                NotificationChannel(FIND, "Find my phone", NotificationManager.IMPORTANCE_HIGH).apply { setSound(null, null) },
                NotificationChannel(ALERTS, "PC battery alerts", NotificationManager.IMPORTANCE_DEFAULT),
                NotificationChannel(PC_MEDIA, "Music playing on your PC", NotificationManager.IMPORTANCE_LOW).apply {
                    setShowBadge(false)
                },
            ),
        )
    }

    fun status(text: String): Notification {
        val open = PendingIntent.getActivity(app, 0, Intent(app, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val sendClip = PendingIntent.getActivity(
            app, 1,
            Intent(app, ClipboardReaderActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
                .putExtra(ClipboardReaderActivity.EXTRA_FORCE, true),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = Notification.Builder(app, STATUS)
            .setSmallIcon(R.drawable.ic_stat)
            .setColor(ACCENT)
            .setContentTitle("Midfielder")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
            .setOnlyAlertOnce(true)
            .addAction(Notification.Action.Builder(null, "Send clipboard", sendClip).build())
        // Android 12+ may delay foreground-service notifications by ~10 s; older versions show them at once.
        if (Build.VERSION.SDK_INT >= 31) builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        return builder.build()
    }

    fun updateStatus(text: String) = nm.notify(STATUS_ID, status(text))

    fun progress(transferId: String, title: String, done: Long, total: Long) {
        val pct = if (total <= 0) 100 else ((done.coerceIn(0, total) * 100) / total).toInt()
        nm.notify(
            transferId.hashCode(),
            Notification.Builder(app, TRANSFERS)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle(title)
                .setColor(ACCENT)
                .setProgress(100, pct, false)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .build(),
        )
    }

    /** A finished transfer. Tapping opens the file ([openRecordId]) or the Files tab. */
    fun finished(transferId: String, title: String, text: String, openRecordId: String? = null) {
        nm.cancel(transferId.hashCode())
        val intent = Intent(app, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            .putExtra(MainActivity.EXTRA_TAB, MainActivity.TAB_FILES)
            .putExtra(MainActivity.EXTRA_OPEN_RECORD, openRecordId)
        val open = PendingIntent.getActivity(
            app, transferId.hashCode(), intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        nm.notify(
            transferId.hashCode(),
            Notification.Builder(app, EVENTS)
                .setSmallIcon(R.drawable.ic_stat)
                .setColor(ACCENT)
                .setContentTitle(title)
                .setContentText(text)
                .setContentIntent(open)
                .setAutoCancel(true)
                .build(),
        )
    }

    /** An image from the PC is on the clipboard; offer to keep it in the gallery. Silent, and goes away by itself. */
    fun clipImage(uri: Uri) {
        val save = PendingIntent.getBroadcast(
            app, CLIP_IMAGE_ID,
            Intent(app, SaveImageReceiver::class.java).setData(uri),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        nm.notify(
            CLIP_IMAGE_ID,
            Notification.Builder(app, CLIPS)
                .setSmallIcon(R.drawable.ic_stat)
                .setColor(ACCENT)
                .setContentTitle("Image copied from PC")
                .setContentText("Paste it anywhere, or save it to your gallery")
                .setTimeoutAfter(CLIP_IMAGE_TIMEOUT_MS)
                .setAutoCancel(true)
                .addAction(Notification.Action.Builder(null, "Save to gallery", save).build())
                .build(),
        )
    }

    fun cancelClipImage() = nm.cancel(CLIP_IMAGE_ID)

    fun cancel(transferId: String) = nm.cancel(transferId.hashCode())

    /** The PC rang this phone; the sound is played by [Ringer], this is just the way to stop it. */
    fun ringing() {
        val stop = PendingIntent.getBroadcast(app, RING_ID, Intent(app, RingStopReceiver::class.java), PendingIntent.FLAG_IMMUTABLE)
        nm.notify(
            RING_ID,
            Notification.Builder(app, FIND)
                .setSmallIcon(R.drawable.ic_ring)
                .setColor(ACCENT)
                .setContentTitle("Your PC is looking for this phone")
                .setContentText("Tap to stop ringing")
                .setCategory(Notification.CATEGORY_ALARM)
                .setContentIntent(stop)
                .setDeleteIntent(stop)
                .setAutoCancel(true)
                .addAction(Notification.Action.Builder(null, "Found it", stop).build())
                .build(),
        )
    }

    fun cancelRinging() = nm.cancel(RING_ID)

    fun batteryAlert(title: String, text: String) = nm.notify(
        BATTERY_ID,
        Notification.Builder(app, ALERTS)
            .setSmallIcon(R.drawable.ic_battery)
            .setColor(ACCENT)
            .setContentTitle(title)
            .setContentText(text)
            .setAutoCancel(true)
            .build(),
    )

    /** "Playing on your PC" with Android's media controls (and the lock screen) driving the PC's player. */
    fun pcMedia(token: android.media.session.MediaSession.Token, state: dev.midfielder.proto.MediaState, art: android.graphics.Bitmap?) {
        fun action(icon: Int, label: String, cmd: dev.midfielder.proto.MediaAction): Notification.Action {
            val pi = PendingIntent.getBroadcast(
                app, PC_MEDIA_ID + cmd.value,
                Intent(app, PcMediaReceiver::class.java).putExtra(PcMediaReceiver.EXTRA_ACTION, cmd.value),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            return Notification.Action.Builder(android.graphics.drawable.Icon.createWithResource(app, icon), label, pi).build()
        }
        val builder = Notification.Builder(app, PC_MEDIA)
            .setSmallIcon(R.drawable.ic_music)
            .setColor(ACCENT)
            .setContentTitle(state.title.ifEmpty { "Playing on your PC" })
            .setContentText(listOf(state.artist, state.app_name).filter { it.isNotEmpty() }.joinToString(" · "))
            .setSubText("On your PC")
            .setLargeIcon(art)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .addAction(action(R.drawable.ic_skip_previous, "Previous", dev.midfielder.proto.MediaAction.MEDIA_ACTION_PREVIOUS))
            .addAction(
                if (state.playing) action(R.drawable.ic_pause, "Pause", dev.midfielder.proto.MediaAction.MEDIA_ACTION_PAUSE)
                else action(R.drawable.ic_play, "Play", dev.midfielder.proto.MediaAction.MEDIA_ACTION_PLAY),
            )
            .addAction(action(R.drawable.ic_skip_next, "Next", dev.midfielder.proto.MediaAction.MEDIA_ACTION_NEXT))
            .setStyle(Notification.MediaStyle().setMediaSession(token).setShowActionsInCompactView(0, 1, 2))
        nm.notify(PC_MEDIA_ID, builder.build())
    }

    fun cancelPcMedia() = nm.cancel(PC_MEDIA_ID)

    companion object {
        const val STATUS_ID = 1
        private const val STATUS = "status"
        private const val TRANSFERS = "transfers"
        private const val EVENTS = "events"
        private const val CLIPS = "clips"
        private const val FIND = "find"
        private const val ALERTS = "alerts"
        private const val PC_MEDIA = "pc_media"
        private const val RING_ID = 3
        private const val BATTERY_ID = 4
        private const val PC_MEDIA_ID = 100
        private const val CLIP_IMAGE_ID = 2
        private const val CLIP_IMAGE_TIMEOUT_MS = 2 * 60_000L
        private const val ACCENT = 0xFF1F6F5C.toInt()
    }
}
