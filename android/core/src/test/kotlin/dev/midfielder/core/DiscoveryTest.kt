package dev.midfielder.core

import dev.midfielder.core.discovery.Backoff
import dev.midfielder.core.discovery.Knock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class DiscoveryTest {
    @Test
    fun `knock round trip and rejection of junk`() {
        val k = Knock("00112233445566778899aabbccddeeff", 47810)
        assertEquals(k, Knock.decode(k.encode()))
        assertNull(Knock.decode(ByteArray(22)))
        assertNull(Knock.decode(k.encode().copyOf(21)))
        assertNull(Knock.decode(k.encode() + 0))
        assertNull(Knock.decode(k.encode(), 21))
    }

    @Test
    fun `backoff grows to the cap and resets`() {
        val b = Backoff(initialMs = 100, maxMs = 1_000, jitter = 0.0)
        assertEquals(listOf(100L, 200L, 400L, 800L, 1000L, 1000L), List(6) { b.nextDelayMs() })
        b.reset()
        assertEquals(100L, b.nextDelayMs())
        val j = Backoff(initialMs = 1000, maxMs = 1000, jitter = 0.2)
        repeat(100) { assertTrue(j.nextDelayMs() in 800L..1200L) }
    }
}
