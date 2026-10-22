package dev.midfielder.android

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.WindowManager
import android.widget.Toast

/**
 * Invisible, never in Recents. Android lets an app read the clipboard only while one of its windows has
 * input focus, so this activity takes focus for a moment, reads, hands the content over and closes.
 */
class ClipboardReaderActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
        window.setLayout(1, 1)
        // If focus never arrives (e.g. the lock screen is up), give up quickly instead of lingering.
        handler.postDelayed({ finishQuietly() }, FOCUS_TIMEOUT_MS)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || done) return
        val force = intent.getBooleanExtra(EXTRA_FORCE, false)
        val engine = SyncEngine.current
        // Take only what's on the clipboard here (cheap), then let go of focus at once: an image is decoded
        // later, off this window, so the user's keyboard and the app underneath are never held up.
        val grab = runCatching { ClipboardBridge(this).grab(force) }
            .onFailure { EventLog.error("Reading the clipboard failed", it) }
            .getOrNull()
        val app = applicationContext
        when {
            engine == null -> {
                // Text can wait in the service's start intent; an image would be too big for that.
                val text = ((grab as? ClipboardBridge.Grab.Ready)?.content as? dev.midfielder.core.clip.ClipContent.Text)?.text
                if (force && text != null) {
                    SyncService.sendText(this, text)
                } else {
                    SyncService.start(this)
                    if (force) Toast.makeText(app, "Midfielder is starting. Try again in a moment.", Toast.LENGTH_SHORT).show()
                }
            }
            grab == null -> {
                EventLog.add("Clipboard is empty, unchanged or unreadable")
                if (force) Toast.makeText(app, "The clipboard is empty", Toast.LENGTH_SHORT).show()
            }
            else -> engine.onClipboardGrab(grab, force) { result ->
                if (force) Toast.makeText(app, result, Toast.LENGTH_SHORT).show()
            }
        }
        finishQuietly()
    }

    private fun finishQuietly() {
        if (done) return
        done = true
        handler.removeCallbacksAndMessages(null)
        finish()
        if (Build.VERSION.SDK_INT >= 34) {
            overrideActivityTransition(OVERRIDE_TRANSITION_CLOSE, 0, 0)
        } else {
            @Suppress("DEPRECATION")
            overridePendingTransition(0, 0)
        }
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    companion object {
        /** Set by explicit user actions (tile, notification button): send even if unchanged, and show the result. */
        const val EXTRA_FORCE = "force"
        private const val FOCUS_TIMEOUT_MS = 1_500L
    }
}
