package dev.midfielder.core

import dev.midfielder.core.clip.ClipContent
import dev.midfielder.core.clip.ClipResult
import dev.midfielder.core.clip.ClipSync
import dev.midfielder.core.clip.ClipboardHistory
import dev.midfielder.proto.ClipboardItem
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import okio.ByteString.Companion.toByteString
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ClipSyncTest {
    private class Rig(scope: kotlinx.coroutines.CoroutineScope, id: String, var now: Long = 1_000) {
        val sent = mutableListOf<ClipboardItem>()
        val applied = mutableListOf<ClipContent>()
        val sync = ClipSync(id, scope, { sent += it }, { c, _ -> applied += c }, { now })
    }

    @Test
    fun `text flows both ways without echo`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        val phone = Rig(backgroundScope, "phone")

        assertEquals(ClipResult.SENT, pc.sync.onLocalChange(ClipContent.Text("hello")))
        assertEquals(1, pc.sent.size)
        assertEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sent.single()))
        assertEquals("hello", (phone.applied.single() as ClipContent.Text).text)

        // Writing to the phone clipboard fires its change listener: that must not bounce back.
        assertNotEquals(ClipResult.SENT, phone.sync.onLocalChange(ClipContent.Text("hello")))
        assertTrue(phone.sent.isEmpty())

        // The same text arriving again is ignored too.
        assertNotEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sent.single()))
    }

    @Test
    fun `copying applied content again after another copy sends it`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        val phone = Rig(backgroundScope, "phone")
        pc.sync.onLocalChange(ClipContent.Text("from pc"))
        assertEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sent.single()))
        assertEquals(ClipResult.DUPLICATE, phone.sync.onLocalChange(ClipContent.Text("from pc")))
        assertEquals(ClipResult.SENT, phone.sync.onLocalChange(ClipContent.Text("typed on phone")))
        assertEquals(ClipResult.SENT, phone.sync.onLocalChange(ClipContent.Text("from pc")))
    }

    @Test
    fun `repeated local copies of the same content send once`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        assertEquals(ClipResult.SENT, pc.sync.onLocalChange(ClipContent.Text("a")))
        assertNotEquals(ClipResult.SENT, pc.sync.onLocalChange(ClipContent.Text("a")))
        assertEquals(ClipResult.SENT, pc.sync.onLocalChange(ClipContent.Text("b")))
        assertEquals(listOf("a", "b"), pc.sent.map { it.inline_data.utf8() })
    }

    @Test
    fun `tampered items are rejected`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        val phone = Rig(backgroundScope, "phone")
        pc.sync.onLocalChange(ClipContent.Text("real"))
        val item = pc.sent.single()
        assertNotEquals(ClipResult.APPLIED, phone.sync.onRemote(item.copy(inline_data = "fake".toByteArray().toByteString(), size = 4)))
        assertNotEquals(ClipResult.APPLIED, phone.sync.onRemote(item.copy(size = 999)))
        assertTrue(phone.applied.isEmpty())
    }

    @Test
    fun `catch up applies only newer content`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc", now = 1_000)
        val phone = Rig(backgroundScope, "phone", now = 2_000)
        pc.sync.onLocalChange(ClipContent.Text("old on pc"))
        phone.sync.onLocalChange(ClipContent.Text("newer on phone"))

        // Reconnect: both send their latest as catch-up.
        assertNotEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sync.catchUpItem()!!))
        assertEquals(ClipResult.APPLIED, pc.sync.onRemote(phone.sync.catchUpItem()!!))
        assertEquals("newer on phone", (pc.applied.single() as ClipContent.Text).text)
    }

    @Test
    fun `sensitive items sync but are never kept in history`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        val phone = Rig(backgroundScope, "phone")
        pc.sync.onLocalChange(ClipContent.Text("hunter2", sensitive = true))
        assertEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sent.single()))
        assertEquals(0, pc.sync.history.size)
        assertEquals(0, phone.sync.history.size)
    }

    @Test
    fun `disabled sync neither sends nor applies`() = runTest(UnconfinedTestDispatcher()) {
        val pc = Rig(backgroundScope, "pc")
        val phone = Rig(backgroundScope, "phone")
        pc.sync.onLocalChange(ClipContent.Text("x"))
        phone.sync.enabled = false
        assertNotEquals(ClipResult.APPLIED, phone.sync.onRemote(pc.sent.single()))
        assertNotEquals(ClipResult.SENT, phone.sync.onLocalChange(ClipContent.Text("y")))
    }

    @Test
    fun `history is bounded by count and bytes`() {
        val h = ClipboardHistory(maxItems = 3, maxTotalBytes = 10)
        for (s in listOf("aa", "bb", "cc", "dd")) h.add(ClipboardHistory.Entry(ClipContent.Text(s), "x", 0))
        assertEquals(listOf("dd", "cc", "bb"), h.snapshot().map { (it.content as ClipContent.Text).text })
        h.add(ClipboardHistory.Entry(ClipContent.Text("123456789"), "x", 0))
        assertTrue(h.bytes <= 10)
        assertEquals("123456789", (h.snapshot().first().content as ClipContent.Text).text)
        h.add(ClipboardHistory.Entry(ClipContent.Text("way too large for the cap"), "x", 0))
        assertTrue(h.bytes <= 10)
    }

    @Test
    fun `re-adding moves to front without double counting`() {
        val h = ClipboardHistory(maxItems = 10, maxTotalBytes = 100)
        h.add(ClipboardHistory.Entry(ClipContent.Text("a"), "x", 0))
        h.add(ClipboardHistory.Entry(ClipContent.Text("b"), "x", 0))
        h.add(ClipboardHistory.Entry(ClipContent.Text("a"), "x", 0))
        assertEquals(listOf("a", "b"), h.snapshot().map { (it.content as ClipContent.Text).text })
        assertEquals(2L, h.bytes)
    }
}
