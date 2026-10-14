package dev.midfielder.android

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * What the app did recently, for the "Recent activity" panel and `adb logcat -s Midfielder`.
 * Bounded to the last [MAX] lines; never records clipboard contents, only kinds and sizes.
 */
object EventLog {
    private const val TAG = "Midfielder"
    private const val MAX = 50
    private val time = SimpleDateFormat("HH:mm:ss", Locale.US)
    private val _lines = MutableStateFlow<List<String>>(emptyList())
    val lines: StateFlow<List<String>> get() = _lines

    @Synchronized
    fun add(message: String) {
        Log.i(TAG, message)
        _lines.value = (listOf("${time.format(Date())}  $message") + _lines.value).take(MAX)
    }

    fun error(message: String, e: Throwable) {
        Log.w(TAG, message, e)
        add("$message: ${e.javaClass.simpleName}: ${e.message}")
    }
}
