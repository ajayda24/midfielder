package dev.midfielder.android

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * What the app did recently, for the Developer tab and `adb logcat -s Midfielder`.
 * Bounded to the last [MAX] entries; never records clipboard contents, only kinds and sizes.
 */
object EventLog {
    data class Entry(val time: String, val message: String, val isError: Boolean) {
        override fun toString() = "$time  $message"
    }

    private const val TAG = "Midfielder"
    private const val MAX = 200
    private val time = SimpleDateFormat("HH:mm:ss", Locale.US)
    private val _entries = MutableStateFlow<List<Entry>>(emptyList())
    val entries: StateFlow<List<Entry>> get() = _entries

    fun add(message: String) = append(message, isError = false)

    fun error(message: String, e: Throwable) {
        Log.w(TAG, message, e)
        append("$message: ${e.javaClass.simpleName}: ${e.message}", isError = true)
    }

    @Synchronized
    private fun append(message: String, isError: Boolean) {
        if (!isError) Log.i(TAG, message)
        _entries.value = (listOf(Entry(time.format(Date()), message, isError)) + _entries.value).take(MAX)
    }

    @Synchronized
    fun clear() {
        _entries.value = emptyList()
    }
}
