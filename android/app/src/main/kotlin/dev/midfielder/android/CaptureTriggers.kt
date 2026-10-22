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

/**
 * Ways to learn that the user copied something (Android 10+ hides clipboard changes from background apps).
 * See docs/ANDROID_BACKGROUND.md. Each trigger only *detects*; reading happens in [ClipboardReaderActivity].
 *
 *  1. Clipboard listener: fires while our UI is in front (and on some OEM builds always).
 *  2. Logcat (needs READ_LOGS, granted once over ADB): Android logs "Denying clipboard access to <pkg>"
 *     every time the clipboard changes while we're in the background. Android 13+ also asks the user to
 *     approve log access each time a reader starts (only possible while Midfielder is on screen), so the
 *     approved reader keeps running. It only reads filtered lines and costs no wakeups of its own.
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
    private val readerLock = Any()
    private var logcatJob: Job? = null
    private var logcatProcess: Process? = null
    private var generation = 0
    private var readerStartedAt = 0L
    private var lastFire = 0L

    /**
     * True once the log reader has seen a line from another process, i.e. Android approved access.
     * Android 13+ asks the user ("Allow Midfielder to access all device logs?") every time a log reader
     * starts, and only while Midfielder is on screen; started in the background it silently sees only our
     * own lines. So an approved reader is kept running for good instead of being restarted.
     */
    @Volatile
    var logAccessActive: Boolean = false
        private set

    val hasReadLogs: Boolean
        get() = app.checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED

    fun start() {
        clipboard.addPrimaryClipChangedListener(listener)
        CopyAccessibilityService.onCopy = { fire("accessibility") }
        ensureLogReader()
    }

    /**
     * Starts the log reader if it isn't running yet. Call when Midfielder's screen is in front, so Android can
     * show its approval prompt. A reader that is running but not approved is restarted then, for a new prompt.
     */
    fun ensureLogReader(inForeground: Boolean = false, userAsked: Boolean = false) {
        if (!hasReadLogs) return
        synchronized(readerLock) {
            val running = logcatJob?.isActive == true
            if (running && (logAccessActive || !inForeground)) return
            // A reader started moments ago may still be waiting for the user to answer Android's prompt.
            if (running && SystemClock.elapsedRealtime() - readerStartedAt < PROMPT_GRACE_MS) return
            // A reader that was shown the prompt and still isn't approved: the user said no (or ignored it).
            if (running && promptedInForeground) refusals++
            if (userAsked) refusals = 0
            // After two refusals, stop asking every time Midfielder opens; the Settings "Ask" button asks again.
            if (inForeground && refusals >= MAX_REFUSALS) return
            stopLogcatLocked()
            startLogcatLocked()
            promptedInForeground = inForeground
        }
    }

    private val prefs = app.getSharedPreferences("capture", Context.MODE_PRIVATE)
    private var promptedInForeground = false
    private var refusals: Int
        get() = prefs.getInt("log_refusals", 0)
        set(v) = prefs.edit().putInt("log_refusals", v).apply()

    /** The user turned down Android's log-access prompt and Midfielder stopped asking on its own. */
    val logAccessRefused: Boolean get() = hasReadLogs && !logAccessActive && refusals >= MAX_REFUSALS

    fun stop() {
        clipboard.removePrimaryClipChangedListener(listener)
        CopyAccessibilityService.onCopy = null
        synchronized(readerLock) { stopLogcatLocked() }
    }

    private fun fire(source: String) {
        val now = SystemClock.elapsedRealtime()
        if (now - lastFire < DEBOUNCE_MS) return
        lastFire = now
        onTrigger(source)
    }

    /**
     * Each reader has its own generation: a reader being stopped can never touch the state of the one that
     * replaced it, and its "stream closed" error on stop is expected, not a failure.
     */
    private fun startLogcatLocked() {
        val needle = "Denying clipboard access to ${app.packageName}"
        val myPid = android.os.Process.myPid()
        val gen = ++generation
        readerStartedAt = SystemClock.elapsedRealtime()
        logAccessActive = false
        logcatJob = scope.launch(Dispatchers.IO) {
            val process = try {
                // -T 1: start at the newest entry. ClipboardService errors are the copy signal; ActivityTaskManager
                // lines (activity starts, rare) only prove that Android approved access to other apps' logs.
                ProcessBuilder(
                    "logcat", "-T", "1", "-v", "brief", "ClipboardService:E", "ActivityTaskManager:I", "*:S",
                ).redirectErrorStream(true).start()
            } catch (e: Exception) {
                EventLog.error("Copy detection: could not start the log reader", e)
                return@launch
            }
            synchronized(readerLock) {
                if (gen != generation) {
                    process.destroy()
                    return@launch
                }
                logcatProcess = process
            }
            EventLog.add("Copy detection: log reader started (approve the prompt if Android shows one)")
            try {
                process.inputStream.bufferedReader().use { reader ->
                    while (isActive) {
                        val line = reader.readLine() ?: break
                        if (gen != generation) break
                        if (!logAccessActive && pidOf(line).let { it != null && it != myPid }) {
                            logAccessActive = true
                            EventLog.add("Copy detection: active (automatic phone → PC)")
                        }
                        if (line.contains(needle)) fire("logcat")
                    }
                }
            } catch (e: java.io.IOException) {
                // The stream is closed when the reader is stopped; anything else is logged, never thrown.
                if (gen == generation) EventLog.error("Copy detection: log reader failed", e)
            } finally {
                process.destroy()
                synchronized(readerLock) {
                    if (gen == generation) {
                        if (logAccessActive) EventLog.add("Copy detection: log reader stopped")
                        logAccessActive = false
                        logcatProcess = null
                    }
                }
            }
        }
    }

    /** "-v brief" lines look like "E/ClipboardService( 1234): ...". */
    private fun pidOf(line: String): Int? {
        val open = line.indexOf('(')
        val close = line.indexOf(')', open + 1)
        if (open < 0 || close < 0) return null
        return line.substring(open + 1, close).trim().toIntOrNull()
    }

    private fun stopLogcatLocked() {
        generation++ // the old reader sees this and exits quietly
        logcatProcess?.destroy() // unblocks readLine()
        logcatProcess = null
        logcatJob?.cancel()
        logcatJob = null
        logAccessActive = false
    }

    private companion object {
        const val DEBOUNCE_MS = 300L
        const val PROMPT_GRACE_MS = 15_000L
        const val MAX_REFUSALS = 2
    }
}
