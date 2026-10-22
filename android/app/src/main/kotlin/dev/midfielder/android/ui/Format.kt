package dev.midfielder.android.ui

import android.text.format.DateUtils
import java.util.Locale

object Format {
    fun bytes(n: Long): String = when {
        n < 1024 -> "$n B"
        n < 1024 * 1024 -> String.format(Locale.US, "%.0f KB", n / 1024.0)
        n < 1024L * 1024 * 1024 -> String.format(Locale.US, "%.1f MB", n / (1024.0 * 1024))
        else -> String.format(Locale.US, "%.2f GB", n / (1024.0 * 1024 * 1024))
    }

    /** "Just now" under a minute (Android would say "0 min. ago"), then "5 min. ago", "Yesterday", a date. */
    fun time(ms: Long, nowMs: Long = System.currentTimeMillis()): String =
        if (nowMs - ms in 0 until DateUtils.MINUTE_IN_MILLIS) "Just now"
        else DateUtils.getRelativeTimeSpanString(ms, nowMs, DateUtils.MINUTE_IN_MILLIS, DateUtils.FORMAT_ABBREV_RELATIVE).toString()
}
