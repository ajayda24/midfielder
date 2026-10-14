package dev.midfielder.android

import android.app.Service
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.IBinder

/**
 * The one long-lived component: a foreground service (type connectedDevice) that owns the [SyncEngine].
 * Android keeps it alive; the engine inside does nothing until an event arrives.
 */
class SyncService : Service() {
    private var engine: SyncEngine? = null

    override fun onCreate() {
        super.onCreate()
        val notifications = Notifications(this)
        startForeground(
            Notifications.STATUS_ID, notifications.status("Starting…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
        engine = SyncEngine(this).also {
            SyncEngine.current = it
            it.start()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_SEND_URIS) {
            // URIs arrive with a read grant attached to this intent, which lasts while the service runs.
            val uris = intent.clipData?.let { clip -> (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri } }.orEmpty()
            engine?.sendUris(uris)
        }
        return START_STICKY
    }

    override fun onDestroy() {
        SyncEngine.current = null
        engine?.close()
        engine = null
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val ACTION_SEND_URIS = "dev.midfielder.SEND_URIS"

        fun start(context: Context) {
            context.startForegroundService(Intent(context, SyncService::class.java))
        }

        fun sendUris(context: Context, uris: List<Uri>) {
            if (uris.isEmpty()) return
            val clip = ClipData.newRawUri("files", uris[0]).apply { for (u in uris.drop(1)) addItem(ClipData.Item(u)) }
            val intent = Intent(context, SyncService::class.java).setAction(ACTION_SEND_URIS).apply {
                clipData = clip
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startForegroundService(intent)
        }
    }
}
