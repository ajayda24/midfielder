package dev.midfielder.core.wire

import dev.midfielder.core.ProtocolException
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/**
 * Length-prefixed framing: a 4-byte big-endian length N followed by N payload bytes.
 *
 * The length is validated against [maxFrameBytes] before the payload buffer is allocated.
 */
public object FrameCodec {
    public const val HEADER_BYTES: Int = 4

    public fun writeFrame(out: OutputStream, payload: ByteArray, maxFrameBytes: Int) {
        val n = payload.size
        if (n < 1 || n > maxFrameBytes) throw ProtocolException("refusing to send frame of $n bytes")
        out.write(n ushr 24 and 0xFF)
        out.write(n ushr 16 and 0xFF)
        out.write(n ushr 8 and 0xFF)
        out.write(n and 0xFF)
        out.write(payload)
    }

    /**
     * Reads one frame. Returns null on a clean end-of-stream at a frame boundary.
     * Throws [ProtocolException] for an invalid length and [EOFException] for a truncated frame.
     */
    public fun readFrame(input: InputStream, maxFrameBytes: Int): ByteArray? {
        val b0 = input.read()
        if (b0 < 0) return null
        val b1 = input.read()
        val b2 = input.read()
        val b3 = input.read()
        if ((b1 or b2 or b3) < 0) throw EOFException("truncated frame header")
        // Read as an unsigned 32-bit value in a Long so a huge length can't wrap to a small or negative Int.
        val n = (b0.toLong() shl 24) or (b1.toLong() shl 16) or (b2.toLong() shl 8) or b3.toLong()
        if (n < 1 || n > maxFrameBytes) throw ProtocolException("frame length $n outside 1..$maxFrameBytes")
        val payload = ByteArray(n.toInt())
        readFully(input, payload)
        return payload
    }

    internal fun readFully(input: InputStream, buf: ByteArray, off: Int = 0, len: Int = buf.size) {
        var read = 0
        while (read < len) {
            val r = input.read(buf, off + read, len - read)
            if (r < 0) throw EOFException("stream ended after $read of $len bytes")
            read += r
        }
    }
}
