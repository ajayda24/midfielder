package dev.midfielder.core

import dev.midfielder.core.history.Direction
import dev.midfielder.core.history.RecordStatus
import dev.midfielder.core.history.Stats
import dev.midfielder.core.history.TransferHistory
import dev.midfielder.core.history.TransferRecord
import dev.midfielder.core.history.TransferTracker
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.time.ZoneOffset

class HistoryTest {
    private fun rec(id: String, name: String = "f$id.jpg") =
        TransferRecord(id, Direction.RECEIVED, name, "image/jpeg", 10, "PC", 1000, RecordStatus.DONE, "content://x/$id")

    @Test
    fun `history is newest first and bounded`() {
        val h = TransferHistory(maxItems = 3)
        for (i in 1..5) h.add(rec("$i"))
        assertEquals(listOf("5", "4", "3"), h.snapshot().map { it.id })
        h.add(rec("4"))
        assertEquals(listOf("4", "5", "3"), h.snapshot().map { it.id })
        assertEquals(2, h.remove(listOf("5", "3", "nope")))
        assertEquals(listOf("4"), h.snapshot().map { it.id })
    }

    @Test
    fun `history round-trips awkward names and skips garbage`() {
        val h = TransferHistory()
        h.add(rec("a", "tab\there\nnew\\line ünï 📷.png"))
        h.add(rec("b", ""))
        val text = h.encode() + "garbage line\nb\tNOPE\tx\tx\t1\tx\t1\tDONE\tx\n"
        val back = TransferHistory.decode(text)
        assertEquals(h.snapshot(), back.snapshot())
        assertTrue(TransferHistory.decode("not a history").snapshot().isEmpty())
        assertEquals(1, TransferHistory.decode(h.encode(), maxItems = 1).snapshot().size)
    }

    @Test
    fun `stats bucket per day and round-trip`() {
        val day = 86_400_000L
        val s = Stats(keepDays = 3)
        s.recordClip(Direction.SENT, 0, ZoneOffset.UTC)
        s.recordClip(Direction.RECEIVED, day, ZoneOffset.UTC)
        s.recordFiles(Direction.RECEIVED, 2, 500, day, ZoneOffset.UTC)
        s.recordFiles(Direction.SENT, 1, 100, 5 * day, ZoneOffset.UTC)
        assertEquals(Stats.Counts(1, 1, 1, 2, 100, 500), s.totals())
        val last = s.lastDays(7, 5 * day, ZoneOffset.UTC)
        assertEquals(7, last.size)
        assertEquals(1L, last[6].filesSent)
        assertEquals(3L, last[2].clips + last[2].files)
        assertEquals(0L, last[0].clips)
        val back = Stats.decode(s.encode(), keepDays = 3)
        assertEquals(s.totals(), back.totals())
        assertEquals(s.lastDays(7, 5 * day, ZoneOffset.UTC), back.lastDays(7, 5 * day, ZoneOffset.UTC))
        assertEquals(Stats.Counts(), Stats.decode("MFS1\nT\t-1\t0\t0\t0\t0\t0\n").totals())
    }

    @Test
    fun `tracker throttles progress but always publishes completion`() {
        var now = 0L
        val t = TransferTracker({ now }, minIntervalMs = 100)
        t.start("x", Direction.SENT, "a.zip", "PC", 1000)
        assertFalse(t.progress("x", 10, 1000))
        now = 150
        assertTrue(t.progress("x", 500, 1000))
        assertEquals(50, t.active.value.single().percent)
        assertTrue(t.progress("x", 1000, 1000))
        assertFalse(t.progress("unknown", 1, 2))
        t.finish("x")
        assertTrue(t.active.value.isEmpty())
    }
}
