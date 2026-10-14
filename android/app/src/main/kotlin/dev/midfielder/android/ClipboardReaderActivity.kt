package dev.midfielder.android

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.WindowManager

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
        val engine = SyncEngine.current
        if (engine != null) {
            val content = runCatching { ClipboardBridge(this).read() }
                .onFailure { EventLog.error("Reading the clipboard failed", it) }
                .getOrNull()
            if (content != null) engine.onLocalClipboard(content) else EventLog.add("Clipboard is empty or unreadable")
        } else {
            SyncService.start(this)
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

    private companion object {
        const val FOCUS_TIMEOUT_MS = 1_500L
    }
}
