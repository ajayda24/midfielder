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
        Log.w(TAG, redact(message), e)
        append("$message: ${e.javaClass.simpleName}: ${e.message}", isError = true)
    }

    @Synchronized
    private fun append(message: String, isError: Boolean) {
        if (!isError) Log.i(TAG, redact(message))
        _entries.value = (listOf(Entry(time.format(Date()), message, isError)) + _entries.value).take(MAX)
    }

    private val uri = Regex("""(content|file)://\S+""")
    private val ipv4 = Regex("""\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b""")
    private val ipv6 = Regex("""\[?\b[0-9a-fA-F]{1,4}(:[0-9a-fA-F]{0,4}){2,7}(%\w+)?\]?(:\d+)?""")

    /**
     * Logcat is readable by other tools (and bug reports): leave out which files and addresses were involved.
     * The in-app log keeps them, for the Developer tab.
     */
    private fun redact(s: String): String = s.replace(uri, "$1://…").replace(ipv6, "<addr>").replace(ipv4, "<addr>")

    @Synchronized
    fun clear() {
        _entries.value = emptyList()
    }
}
