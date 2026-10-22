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

    private val history: TransferHistory = load(historyFile, TransferHistory::decode) ?: TransferHistory()
    val stats: Stats = load(statsFile, Stats::decode) ?: Stats()

    /** Bumped on every change; the UI re-reads [records] when it changes. */
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> get() = _version

    fun records(): List<TransferRecord> = history.snapshot()

    fun add(records: List<TransferRecord>) {
        if (records.isEmpty()) return
        for (r in records.asReversed()) history.add(r)
        changed()
    }

    /** Puts removed records back in time order (Undo). */
    fun restore(records: List<TransferRecord>) {
        if (records.isEmpty()) return
        // One lock around clear-and-refill (TransferHistory locks on itself), so a save never sees it half empty.
        synchronized(history) {
            val merged = (history.snapshot() + records).distinctBy { it.id }.sortedByDescending { it.timeMs }
            history.clear()
            for (r in merged.asReversed()) history.add(r)
        }
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

    /** Called from the save timer and from the service on shutdown: one writer at a time. */
    @Synchronized
    fun saveNow() {
        runCatching {
            write(historyFile, history.encode())
            write(statsFile, stats.encode())
        }.onFailure { EventLog.error("Saving history failed", it) }
    }

    /** AtomicFile: a write is synced to disk before it replaces the old file, so a power cut keeps one or the other. */
    private fun write(file: File, text: String) {
        val atomic = android.util.AtomicFile(file)
        val out = atomic.startWrite()
        try {
            out.write(text.toByteArray())
            atomic.finishWrite(out)
        } catch (e: Exception) {
            atomic.failWrite(out)
            throw e
        }
    }

    private fun <T> load(file: File, decode: (String) -> T): T? {
        legacyTemp(file)
        val atomic = android.util.AtomicFile(file)
        if (!atomic.baseFile.exists()) return null
        return runCatching { decode(String(atomic.readFully())) }
            .onFailure { EventLog.error("${file.name} couldn't be read; starting fresh", it) }
            .getOrNull()
    }

    /** Older versions wrote "<name>.tmp" and renamed it: finish a rename a crash interrupted. */
    private fun legacyTemp(file: File) {
        val tmp = File(file.path + ".tmp")
        if (tmp.exists() && !file.exists()) tmp.renameTo(file) else tmp.delete()
    }

    companion object {
        private const val SAVE_DELAY_MS = 1_000L

        @Volatile
        private var instance: HistoryStore? = null

        fun get(context: Context): HistoryStore =
            instance ?: synchronized(this) { instance ?: HistoryStore(context).also { instance = it } }
    }
}
