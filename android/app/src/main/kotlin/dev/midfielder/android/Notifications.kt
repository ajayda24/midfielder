package dev.midfielder.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.DownloadManager
import android.content.Context
import android.content.Intent

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
            ),
        )
    }

    fun status(text: String): Notification {
        val open = PendingIntent.getActivity(app, 0, Intent(app, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val sendClip = PendingIntent.getActivity(
            app, 1,
            Intent(app, ClipboardReaderActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(app, STATUS)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Midfielder")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
            .setOnlyAlertOnce(true)
            .addAction(Notification.Action.Builder(null, "Send clipboard", sendClip).build())
            .setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    fun updateStatus(text: String) = nm.notify(STATUS_ID, status(text))

    fun progress(transferId: String, title: String, done: Long, total: Long) {
        val pct = if (total <= 0) 100 else ((done * 100) / total).toInt()
        nm.notify(
            transferId.hashCode(),
            Notification.Builder(app, TRANSFERS)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle(title)
                .setProgress(100, pct, false)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .build(),
        )
    }

    fun finished(transferId: String, title: String, text: String) {
        nm.cancel(transferId.hashCode())
        val open = PendingIntent.getActivity(
            app, 2, Intent(DownloadManager.ACTION_VIEW_DOWNLOADS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE,
        )
        nm.notify(
            transferId.hashCode(),
            Notification.Builder(app, EVENTS)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle(title)
                .setContentText(text)
                .setContentIntent(open)
                .setAutoCancel(true)
                .build(),
        )
    }

    fun cancel(transferId: String) = nm.cancel(transferId.hashCode())

    companion object {
        const val STATUS_ID = 1
        private const val STATUS = "status"
        private const val TRANSFERS = "transfers"
        private const val EVENTS = "events"
    }
}
