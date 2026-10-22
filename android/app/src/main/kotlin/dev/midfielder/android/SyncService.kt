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
        try {
            startForeground(
                Notifications.STATUS_ID, notifications.status("Starting…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
            )
        } catch (e: Exception) {
            // Android 12+ refuses a foreground start from the background in some cases (ForegroundServiceStartNotAllowedException),
            // and 14+ refuses it without the permission the type needs. Crashing would only loop; ask for a tap instead.
            EventLog.error("Couldn't start in the foreground", e)
            notifications.problem("Midfielder is paused", "Android didn't let it start in the background. Tap to reconnect.")
            stopSelf()
            return
        }
        engine = try {
            SyncEngine(this).also {
                SyncEngine.current = it
                it.start()
            }
        } catch (e: Exception) {
            EventLog.error("Couldn't start", e)
            notifications.problem("Midfielder couldn't start", "Open it to try again. If this keeps happening, reinstall Midfielder.")
            stopSelf()
            null
        }
        engine?.storage?.storageProblem?.let { notifications.problem("Pair with your PC again", it) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (engine == null) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (intent?.action == ACTION_SEND_TEXT) {
            val text = intent.getStringExtra(EXTRA_TEXT)
            if (!text.isNullOrEmpty()) {
                val result = engine?.onLocalClipboard(dev.midfielder.core.clip.ClipContent.Text(text), force = true)
                if (result != null) android.widget.Toast.makeText(this, result, android.widget.Toast.LENGTH_SHORT).show()
            }
        }
        if (intent?.action == ACTION_SEND_URIS) {
            // URIs arrive with a read grant attached to this intent, which lasts while the service runs.
            val uris = intent.clipData?.let { clip -> (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri } }.orEmpty()
            engine?.sendUris(uris, intent.getStringExtra(EXTRA_TARGET))
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
        private const val EXTRA_TARGET = "target"
        private const val ACTION_SEND_TEXT = "dev.midfielder.SEND_TEXT"
        private const val EXTRA_TEXT = "text"

        /** Shared text while the engine isn't running yet: the service sends it once it has started. */
        fun sendText(context: Context, text: String) {
            context.startForegroundService(Intent(context, SyncService::class.java).setAction(ACTION_SEND_TEXT).putExtra(EXTRA_TEXT, text))
        }

        fun start(context: Context) {
            context.startForegroundService(Intent(context, SyncService::class.java))
        }

        /** [target] is a Direct Share shortcut id naming the PC; null means the connected one. */
        fun sendUris(context: Context, uris: List<Uri>, target: String? = null) {
            if (uris.isEmpty()) return
            val clip = ClipData.newRawUri("files", uris[0]).apply { for (u in uris.drop(1)) addItem(ClipData.Item(u)) }
            val intent = Intent(context, SyncService::class.java).setAction(ACTION_SEND_URIS).apply {
                clipData = clip
                putExtra(EXTRA_TARGET, target)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startForegroundService(intent)
        }
    }
}
