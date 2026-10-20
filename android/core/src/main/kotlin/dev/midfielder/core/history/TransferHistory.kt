package dev.midfielder.core.history

public enum class Direction { SENT, RECEIVED }

public enum class RecordStatus { DONE, FAILED, CANCELLED }

/** One finished file, in either direction. [location] is a platform path or URI ("" for sent files). */
public data class TransferRecord(
    val id: String,
    val direction: Direction,
    val name: String,
    val mime: String,
    val size: Long,
    val peer: String,
    val timeMs: Long,
    val status: RecordStatus,
    val location: String,
) {
    val isImage: Boolean get() = mime.startsWith("image/")
}

/**
 * Finished transfers, newest first, bounded to [maxItems]. Persisted as text: one record per line,
 * tab-separated, with tabs/newlines/backslashes escaped. Unreadable lines are skipped, never fatal.
 */
public class TransferHistory(private val maxItems: Int = 500) {
    private val records = ArrayDeque<TransferRecord>()

    init {
        require(maxItems > 0)
    }

    @Synchronized
    public fun add(record: TransferRecord) {
        records.removeAll { it.id == record.id }
        records.addFirst(record)
        while (records.size > maxItems) records.removeLast()
    }

    @Synchronized
    public fun snapshot(): List<TransferRecord> = records.toList()

    /** Removes records by id; returns how many were removed. */
    @Synchronized
    public fun remove(ids: Collection<String>): Int {
        val set = ids.toHashSet()
        val before = records.size
        records.removeAll { it.id in set }
        return before - records.size
    }

    @Synchronized
    public fun clear() {
        records.clear()
    }

    @Synchronized
    public fun encode(): String = buildString {
        append(HEADER).append('\n')
        for (r in records) {
            listOf(
                r.id, r.direction.name, r.name, r.mime, r.size.toString(), r.peer, r.timeMs.toString(),
                r.status.name, r.location,
            ).joinTo(this, "\t") { escape(it) }
            append('\n')
        }
    }

    public companion object {
        private const val HEADER = "MFH1"
        private const val FIELDS = 9

        public fun decode(text: String, maxItems: Int = 500): TransferHistory {
            val h = TransferHistory(maxItems)
            val lines = text.split('\n')
            if (lines.firstOrNull() != HEADER) return h
            for (line in lines.asSequence().drop(1).filter { it.isNotEmpty() }) {
                val f = line.split('\t').map(::unescape)
                if (f.size != FIELDS) continue
                val record = runCatching {
                    TransferRecord(
                        id = f[0], direction = Direction.valueOf(f[1]), name = f[2], mime = f[3], size = f[4].toLong(),
                        peer = f[5], timeMs = f[6].toLong(), status = RecordStatus.valueOf(f[7]), location = f[8],
                    )
                }.getOrNull() ?: continue
                if (h.records.size < maxItems) h.records.addLast(record)
            }
            return h
        }

        private fun escape(s: String): String {
            if (s.none { it == '\\' || it == '\t' || it == '\n' || it == '\r' }) return s
            val b = StringBuilder(s.length + 8)
            for (c in s) {
                when (c) {
                    '\\' -> b.append("\\\\")
                    '\t' -> b.append("\\t")
                    '\n' -> b.append("\\n")
                    '\r' -> b.append("\\r")
                    else -> b.append(c)
                }
            }
            return b.toString()
        }

        private fun unescape(s: String): String {
            if ('\\' !in s) return s
            val b = StringBuilder(s.length)
            var i = 0
            while (i < s.length) {
                val c = s[i]
                if (c == '\\' && i + 1 < s.length) {
                    when (s[i + 1]) {
                        't' -> b.append('\t')
                        'n' -> b.append('\n')
                        'r' -> b.append('\r')
                        else -> b.append(s[i + 1])
                    }
                    i += 2
                } else {
                    b.append(c)
                    i++
                }
            }
            return b.toString()
        }
    }
}
