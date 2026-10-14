package dev.midfielder.android

import android.Manifest
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.SystemClock
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible

/**
 * Ways to learn that the user copied something (Android 10+ hides clipboard changes from background apps).
 * See docs/ANDROID_BACKGROUND.md. Each trigger only *detects*; reading happens in [ClipboardReaderActivity].
 *
 *  1. Clipboard listener: fires while our UI is in front (and on some OEM builds always).
 *  2. Logcat (needs READ_LOGS, granted once over ADB): Android logs "Denying clipboard access to <pkg>"
 *     every time the clipboard changes while we're in the background. Watched only while the screen is on.
 *  3. Accessibility (optional): taps on "Copy" and "Copied" toasts. See [CopyAccessibilityService].
 */
class CaptureTriggers(
    context: Context,
    private val scope: CoroutineScope,
    private val onTrigger: (source: String) -> Unit,
) {
    private val app = context.applicationContext
    private val clipboard = app.getSystemService(ClipboardManager::class.java)
    private val listener = ClipboardManager.OnPrimaryClipChangedListener { fire("listener") }
    private var logcatJob: Job? = null
    private var logcatProcess: Process? = null
    private var lastFire = 0L

    val hasReadLogs: Boolean
        get() = app.checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED

    fun start(screenOn: Boolean) {
        clipboard.addPrimaryClipChangedListener(listener)
        CopyAccessibilityService.onCopy = { fire("accessibility") }
        onScreen(screenOn)
    }

    /** The logcat reader exists only while the screen is on: nobody copies with the screen off. */
    fun onScreen(on: Boolean) {
        if (on && hasReadLogs) startLogcat() else stopLogcat()
    }

    fun stop() {
        clipboard.removePrimaryClipChangedListener(listener)
        CopyAccessibilityService.onCopy = null
        stopLogcat()
    }

    private fun fire(source: String) {
        val now = SystemClock.elapsedRealtime()
        if (now - lastFire < DEBOUNCE_MS) return
        lastFire = now
        onTrigger(source)
    }

    private fun startLogcat() {
        if (logcatJob?.isActive == true) return
        val needle = "Denying clipboard access to ${app.packageName}"
        logcatJob = scope.launch(Dispatchers.IO) {
            val process = runCatching {
                // -T 1: start at the newest entry; only ClipboardService errors pass the filter.
                ProcessBuilder("logcat", "-T", "1", "-v", "brief", "ClipboardService:E", "*:S").redirectErrorStream(true).start()
            }.getOrNull() ?: return@launch
            logcatProcess = process
            try {
                process.inputStream.bufferedReader().use { reader ->
                    while (isActive) {
                        val line = runInterruptible { reader.readLine() } ?: break
                        if (line.contains(needle)) fire("logcat")
                    }
                }
            } finally {
                process.destroy()
            }
        }
    }

    private fun stopLogcat() {
        logcatProcess?.destroy() // unblocks readLine()
        logcatProcess = null
        logcatJob?.cancel()
        logcatJob = null
    }

    private companion object {
        const val DEBOUNCE_MS = 300L
    }
}
