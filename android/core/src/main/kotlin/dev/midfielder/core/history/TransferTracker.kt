package dev.midfielder.core.history

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** A transfer that is still moving bytes, as shown in the UI. */
public data class ActiveTransfer(
    val id: String,
    val direction: Direction,
    val title: String,
    val peer: String,
    val done: Long,
    val total: Long,
) {
    /** 0..100, or -1 while the size is unknown. */
    val percent: Int get() = if (total <= 0) -1 else ((done.coerceIn(0, total) * 100) / total).toInt()
}

/**
 * Live list of running transfers. Progress updates are throttled to one per [minIntervalMs] per transfer, so
 * a fast LAN copy doesn't flood the UI; observers only pay for it while they collect.
 */
public class TransferTracker(private val clock: () -> Long, private val minIntervalMs: Long = 250) {
    private val lastEmit = HashMap<String, Long>()
    private val _active = MutableStateFlow<List<ActiveTransfer>>(emptyList())
    public val active: StateFlow<List<ActiveTransfer>> get() = _active

    @Synchronized
    public fun start(id: String, direction: Direction, title: String, peer: String, total: Long) {
        _active.value = _active.value.filter { it.id != id } + ActiveTransfer(id, direction, title, peer, 0, total)
        lastEmit[id] = clock()
    }

    /** Returns true when the update was published (not throttled). Unknown ids are ignored. */
    @Synchronized
    public fun progress(id: String, done: Long, total: Long): Boolean {
        val list = _active.value
        val i = list.indexOfFirst { it.id == id }
        if (i < 0) return false
        val now = clock()
        if (done < total && now - (lastEmit[id] ?: 0L) < minIntervalMs) return false
        lastEmit[id] = now
        _active.value = list.toMutableList().also { it[i] = it[i].copy(done = done, total = total) }
        return true
    }

    @Synchronized
    public fun finish(id: String) {
        lastEmit.remove(id)
        _active.value = _active.value.filter { it.id != id }
    }

    public fun contains(id: String): Boolean = _active.value.any { it.id == id }
}
