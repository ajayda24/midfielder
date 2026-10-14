package dev.midfielder.android

import android.accessibilityservice.AccessibilityService
import android.content.res.Resources
import android.view.accessibility.AccessibilityEvent

/**
 * Optional helper: notices taps on "Copy" and "Copied" toasts so the copy reaches the PC immediately even
 * without the ADB permission. It never reads window content and keeps nothing.
 */
class CopyAccessibilityService : AccessibilityService() {
    private val copyWords: Set<String> by lazy {
        val sys = Resources.getSystem()
        setOfNotNull(
            runCatching { sys.getString(android.R.string.copy) }.getOrNull(),
            runCatching { sys.getString(android.R.string.copyUrl) }.getOrNull(),
            "Copy", "Copied", "Copied to clipboard",
        ).map { it.lowercase() }.toSet()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        if (event.packageName == packageName) return
        val texts = event.text.map { it.toString().lowercase() } + listOfNotNull(event.contentDescription?.toString()?.lowercase())
        if (texts.any { t -> copyWords.any { w -> t == w || t.startsWith(w) } }) onCopy?.invoke()
    }

    override fun onInterrupt() = Unit

    companion object {
        @Volatile
        var onCopy: (() -> Unit)? = null
    }
}
