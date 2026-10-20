package dev.midfielder.core.history

import java.time.Instant
import java.time.ZoneId

/** Totals plus one bucket per local day, for the small analytics card. Only the last [keepDays] days are kept. */
public class Stats(private val keepDays: Int = 30) {
    public data class Counts(
        val clipsSent: Long = 0,
        val clipsReceived: Long = 0,
        val filesSent: Long = 0,
        val filesReceived: Long = 0,
        val bytesSent: Long = 0,
        val bytesReceived: Long = 0,
    ) {
        val clips: Long get() = clipsSent + clipsReceived
        val files: Long get() = filesSent + filesReceived
        val bytes: Long get() = bytesSent + bytesReceived

        public operator fun plus(o: Counts): Counts = Counts(
            clipsSent + o.clipsSent, clipsReceived + o.clipsReceived, filesSent + o.filesSent,
            filesReceived + o.filesReceived, bytesSent + o.bytesSent, bytesReceived + o.bytesReceived,
        )
    }

    private var total = Counts()
    private val days = sortedMapOf<Long, Counts>()

    @Synchronized
    public fun totals(): Counts = total

    @Synchronized
    public fun recordClip(direction: Direction, atMs: Long, zone: ZoneId = ZoneId.systemDefault()) {
        add(atMs, zone, if (direction == Direction.SENT) Counts(clipsSent = 1) else Counts(clipsReceived = 1))
    }

    @Synchronized
    public fun recordFiles(direction: Direction, count: Int, bytes: Long, atMs: Long, zone: ZoneId = ZoneId.systemDefault()) {
        require(count >= 0 && bytes >= 0)
        add(
            atMs, zone,
            if (direction == Direction.SENT) Counts(filesSent = count.toLong(), bytesSent = bytes)
            else Counts(filesReceived = count.toLong(), bytesReceived = bytes),
        )
    }

    /** The last [n] days ending today, oldest first; days without activity are zero. */
    @Synchronized
    public fun lastDays(n: Int, nowMs: Long, zone: ZoneId = ZoneId.systemDefault()): List<Counts> {
        val today = day(nowMs, zone)
        return (n - 1 downTo 0).map { days[today - it] ?: Counts() }
    }

    @Synchronized
    public fun clear() {
        total = Counts()
        days.clear()
    }

    private fun add(atMs: Long, zone: ZoneId, c: Counts) {
        total += c
        val d = day(atMs, zone)
        days[d] = (days[d] ?: Counts()) + c
        while (days.size > keepDays) days.remove(days.firstKey())
    }

    @Synchronized
    public fun encode(): String = buildString {
        append(HEADER).append('\n')
        append("T\t").append(line(total)).append('\n')
        for ((d, c) in days) append(d).append('\t').append(line(c)).append('\n')
    }

    public companion object {
        private const val HEADER = "MFS1"

        private fun day(ms: Long, zone: ZoneId) = Instant.ofEpochMilli(ms).atZone(zone).toLocalDate().toEpochDay()

        private fun line(c: Counts) =
            listOf(c.clipsSent, c.clipsReceived, c.filesSent, c.filesReceived, c.bytesSent, c.bytesReceived).joinToString("\t")

        public fun decode(text: String, keepDays: Int = 30): Stats {
            val s = Stats(keepDays)
            val lines = text.split('\n')
            if (lines.firstOrNull() != HEADER) return s
            for (l in lines.drop(1)) {
                val f = l.split('\t')
                if (f.size != 7) continue
                val v = f.drop(1).mapNotNull { n -> n.toLongOrNull()?.takeIf { it >= 0 } }
                if (v.size != 6) continue
                val c = Counts(v[0], v[1], v[2], v[3], v[4], v[5])
                if (f[0] == "T") s.total = c else f[0].toLongOrNull()?.let { s.days[it] = c }
            }
            while (s.days.size > keepDays) s.days.remove(s.days.firstKey())
            return s
        }
    }
}
