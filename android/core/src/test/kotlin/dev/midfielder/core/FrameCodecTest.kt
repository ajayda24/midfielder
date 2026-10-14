package dev.midfielder.core

import dev.midfielder.core.wire.Envelopes
import dev.midfielder.core.wire.FrameCodec
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.util.Random

class FrameCodecTest {
    @Test
    fun `round trips frames back to back`() {
        val out = ByteArrayOutputStream()
        val a = byteArrayOf(1, 2, 3)
        val b = ByteArray(70_000) { it.toByte() }
        FrameCodec.writeFrame(out, a, 1 shl 20)
        FrameCodec.writeFrame(out, b, 1 shl 20)
        val input = ByteArrayInputStream(out.toByteArray())
        assertArrayEquals(a, FrameCodec.readFrame(input, 1 shl 20))
        assertArrayEquals(b, FrameCodec.readFrame(input, 1 shl 20))
        assertNull(FrameCodec.readFrame(input, 1 shl 20))
    }

    @Test
    fun `rejects oversized length before allocating`() {
        // 0xFFFFFFFF would be -1 as a signed Int and 4 GiB unsigned: both must be rejected without allocation.
        val huge = byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte())
        assertThrows<ProtocolException> { FrameCodec.readFrame(ByteArrayInputStream(huge), 1024) }
        val justOver = byteArrayOf(0, 0, 4, 1) // 1025
        assertThrows<ProtocolException> { FrameCodec.readFrame(ByteArrayInputStream(justOver), 1024) }
    }

    @Test
    fun `rejects empty frames and refuses to send oversize`() {
        assertThrows<ProtocolException> { FrameCodec.readFrame(ByteArrayInputStream(byteArrayOf(0, 0, 0, 0)), 10) }
        assertThrows<ProtocolException> { FrameCodec.writeFrame(ByteArrayOutputStream(), ByteArray(11), 10) }
        assertThrows<ProtocolException> { FrameCodec.writeFrame(ByteArrayOutputStream(), ByteArray(0), 10) }
    }

    @Test
    fun `truncated header or payload is an EOF error`() {
        assertThrows<EOFException> { FrameCodec.readFrame(ByteArrayInputStream(byteArrayOf(0, 0)), 10) }
        assertThrows<EOFException> { FrameCodec.readFrame(ByteArrayInputStream(byteArrayOf(0, 0, 0, 5, 1, 2)), 10) }
    }

    @Test
    fun `reads frames delivered one byte at a time`() {
        val out = ByteArrayOutputStream()
        val payload = ByteArray(1000) { (it * 7).toByte() }
        FrameCodec.writeFrame(out, payload, 2000)
        val bytes = out.toByteArray()
        val trickle = object : InputStream() {
            var i = 0
            override fun read(): Int = if (i < bytes.size) bytes[i++].toInt() and 0xFF else -1
            override fun read(b: ByteArray, off: Int, len: Int): Int {
                if (i >= bytes.size) return -1
                b[off] = bytes[i++]
                return 1
            }
        }
        assertArrayEquals(payload, FrameCodec.readFrame(trickle, 2000))
    }

    @Test
    fun `random garbage never escapes as anything but a protocol or EOF error`() {
        // A quick random-input pass over the decoder (a coverage-guided fuzzer would be the next step).
        val rnd = Random(1234)
        repeat(20_000) {
            val len = rnd.nextInt(64)
            val data = ByteArray(len).also(rnd::nextBytes)
            try {
                Envelopes.read(ByteArrayInputStream(data), 4096)
            } catch (_: ProtocolException) {
            } catch (_: EOFException) {
            }
        }
    }
}
