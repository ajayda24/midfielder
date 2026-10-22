package dev.midfielder.android

import android.accessibilityservice.AccessibilityService
import android.content.res.Resources
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent

/**
 * Optional helper: notices taps on "Copy" and "Copied" toasts so the copy reaches the PC immediately even
 * without the ADB permission. It never reads window content and keeps nothing.
 */
class CopyAccessibilityService : AccessibilityService() {
    /** Button labels that copy: exact matches only ("Copy" yes, "Copy of report.pdf" or "Copyright" no). */
    private val copyButtons: Set<String> by lazy {
        val sys = Resources.getSystem()
        setOfNotNull(
            runCatching { sys.getString(android.R.string.copy) }.getOrNull(),
            runCatching { sys.getString(android.R.string.copyUrl) }.getOrNull(),
            "Copy", "Copy link", "Copy text", "Copy URL",
        ).map { it.lowercase() }.toSet()
    }

    private val main = Handler(Looper.getMainLooper())
    private val fireLater = Runnable { onCopy?.invoke() }

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        if (event.packageName == packageName) return
        val texts = (event.text.map { it.toString() } + listOfNotNull(event.contentDescription?.toString()))
            .map { it.trim().trimEnd('!', '.').lowercase() }
        when (event.eventType) {
            AccessibilityEvent.TYPE_VIEW_CLICKED, AccessibilityEvent.TYPE_VIEW_LONG_CLICKED ->
                // The tap comes before the app writes the clipboard: read a moment later, or we'd get the old clip.
                if (texts.any { it in copyButtons }) {
                    main.removeCallbacks(fireLater)
                    main.postDelayed(fireLater, AFTER_TAP_MS)
                }
            AccessibilityEvent.TYPE_NOTIFICATION_STATE_CHANGED ->
                // Only toasts ("Copied to clipboard"), not every notification that mentions copying.
                if (event.className?.contains("Toast") == true && texts.any { it.startsWith("copied") }) {
                    main.removeCallbacks(fireLater)
                    onCopy?.invoke()
                }
        }
    }

    override fun onInterrupt() = Unit

    companion object {
        @Volatile
        var onCopy: (() -> Unit)? = null

        private const val AFTER_TAP_MS = 400L
    }
}
