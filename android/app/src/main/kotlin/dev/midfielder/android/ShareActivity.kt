package dev.midfielder.android

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import dev.midfielder.core.clip.ClipContent

/** "Share → Send to PC": text goes to the PC clipboard, files and images go to the PC's Downloads. */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = extractUris(intent)
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
        when {
            uris.isNotEmpty() -> {
                SyncService.sendUris(this, uris)
                Toast.makeText(this, "Sending ${uris.size} file(s) to your PC", Toast.LENGTH_SHORT).show()
            }
            !text.isNullOrEmpty() -> {
                val engine = SyncEngine.current
                if (engine != null) {
                    engine.onLocalClipboard(ClipContent.Text(text))
                    Toast.makeText(this, "Sent to your PC's clipboard", Toast.LENGTH_SHORT).show()
                } else {
                    SyncService.start(this)
                    Toast.makeText(this, "Midfielder is starting, try again in a moment", Toast.LENGTH_SHORT).show()
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
