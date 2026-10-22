package dev.midfielder.android

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService

/** Quick Settings tile: one tap sends the current clipboard to the PC (works with no setup at all). */
class SendClipboardTileService : TileService() {
    override fun onStartListening() {
        val engine = SyncEngine.current
        val status = engine?.status?.value
        qsTile?.apply {
            // Always tappable (sending works the moment the PC connects); the subtitle says where it goes.
            state = Tile.STATE_ACTIVE
            if (Build.VERSION.SDK_INT >= 29) {
                subtitle = when {
                    status == null -> null
                    status.connected.isNotEmpty() -> status.connected.first()
                    status.paired.isEmpty() -> "Not paired"
                    else -> "Not connected"
                }
            }
            updateTile()
        }
    }

    override fun onClick() {
        // On the lock screen, unlock first: the clipboard can't be read while locked.
        if (isLocked) unlockAndRun { sendClipboard() } else sendClipboard()
    }

    private fun sendClipboard() {
        val intent = Intent(this, ClipboardReaderActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
            .putExtra(ClipboardReaderActivity.EXTRA_FORCE, true)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION", "StartActivityAndCollapseDeprecated")
            startActivityAndCollapse(intent)
        }
    }
}
