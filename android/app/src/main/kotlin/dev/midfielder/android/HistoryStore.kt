package dev.midfielder.android

import android.content.Context
import dev.midfielder.core.history.Direction
import dev.midfielder.core.history.Stats
import dev.midfielder.core.history.TransferHistory
import dev.midfielder.core.history.TransferRecord
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import java.io.File

/**
 * File history (last 500) and usage counters, shared by the service and the UI. Saved to app-private storage
 * a second after the last change (one write for a burst of events), and loaded lazily on first use.
 */
class HistoryStore private constructor(context: Context) {
    private val dir = context.applicationContext.filesDir
    private val historyFile = File(dir, "history.txt")
    private val statsFile = File(dir, "stats.txt")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var saveJob: Job? = null

    private val history: TransferHistory = runCatching { TransferHistory.decode(historyFile.readText()) }.getOrElse { TransferHistory() }
    val stats: Stats = runCatching { Stats.decode(statsFile.readText()) }.getOrElse { Stats() }

    /** Bumped on every change; the UI re-reads [records] when it changes. */
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> get() = _version

    fun records(): List<TransferRecord> = history.snapshot()

    fun add(records: List<TransferRecord>) {
        if (records.isEmpty()) return
        for (r in records.asReversed()) history.add(r)
        changed()
    }

    fun remove(ids: Collection<String>) {
        if (history.remove(ids) > 0) changed()
    }

    fun clearHistory() {
        history.clear()
        changed()
    }

    fun clearStats() {
        stats.clear()
        changed()
    }

    fun recordClip(direction: Direction) {
        stats.recordClip(direction, System.currentTimeMillis())
        changed()
    }

    fun recordFiles(direction: Direction, count: Int, bytes: Long) {
        stats.recordFiles(direction, count, bytes, System.currentTimeMillis())
        changed()
    }

    @Synchronized
    private fun changed() {
        _version.value++
        saveJob?.cancel()
        saveJob = scope.launch {
            delay(SAVE_DELAY_MS)
            saveNow()
        }
    }

    fun saveNow() {
        runCatching {
            write(historyFile, history.encode())
            write(statsFile, stats.encode())
        }.onFailure { EventLog.error("Saving history failed", it) }
    }

    private fun write(file: File, text: String) {
        val tmp = File(file.path + ".tmp")
        tmp.writeText(text)
        if (!tmp.renameTo(file)) error("could not save ${file.name}")
    }

    companion object {
        private const val SAVE_DELAY_MS = 1_000L

        @Volatile
        private var instance: HistoryStore? = null

        fun get(context: Context): HistoryStore =
            instance ?: synchronized(this) { instance ?: HistoryStore(context).also { instance = it } }
    }
}
