package dev.midfielder.android

import android.app.Activity
import android.content.Intent
import android.content.pm.ShortcutManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import dev.midfielder.core.clip.ClipContent

/**
 * "Share → Send to PC" (or the PC's Direct Share icon): text goes to the PC clipboard, files and images go to
 * the PC's Downloads.
 */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Only content from other apps: no file:// paths (they bypass Android's sharing permissions) and none of
        // our own providers (a crafted share could otherwise send Midfielder's private files).
        val uris = extractUris(intent).filter { it.scheme == "content" && it.authority?.startsWith(packageName) != true && it.authority != "dev.midfielder.clipimages" }
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
        val target = intent.getStringExtra(Intent.EXTRA_SHORTCUT_ID)
        // Helps Android rank the PC first in the share sheet next time.
        target?.let { id -> runCatching { getSystemService(ShortcutManager::class.java)?.reportShortcutUsed(id) } }
        when {
            uris.isEmpty() && intent.action != Intent.ACTION_SEND -> Toast.makeText(this, "Nothing Midfielder can send", Toast.LENGTH_SHORT).show()
            uris.isNotEmpty() -> {
                SyncService.sendUris(this, uris, target)
                val what = if (uris.size == 1) "file" else "${uris.size} files"
                Toast.makeText(this, "Sending $what to your PC", Toast.LENGTH_SHORT).show()
            }
            !text.isNullOrEmpty() -> {
                val engine = SyncEngine.current
                if (engine != null) {
                    Toast.makeText(this, engine.onLocalClipboard(ClipContent.Text(text), force = true), Toast.LENGTH_SHORT).show()
                } else {
                    SyncService.sendText(this, text)
                }
            }
        }
        finish()
    }

    private fun extractUris(intent: Intent): List<Uri> = when (intent.action) {
        Intent.ACTION_SEND -> listOfNotNull(
            if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_STREAM),
        )
        Intent.ACTION_SEND_MULTIPLE ->
            (
                if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
                else @Suppress("DEPRECATION") intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)
                ).orEmpty()
        else -> emptyList()
    }
}
