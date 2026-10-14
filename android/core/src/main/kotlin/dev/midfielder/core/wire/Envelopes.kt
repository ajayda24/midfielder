package dev.midfielder.core.wire

import dev.midfielder.core.ProtocolException
import dev.midfielder.proto.Envelope
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/** Encodes/decodes [Envelope]s on top of [FrameCodec]. Not thread-safe per stream; callers serialize access. */
public object Envelopes {
    public fun write(out: OutputStream, envelope: Envelope, maxFrameBytes: Int) {
        FrameCodec.writeFrame(out, Envelope.ADAPTER.encode(envelope), maxFrameBytes)
    }

    /** Returns null on a clean end-of-stream. */
    public fun read(input: InputStream, maxFrameBytes: Int): Envelope? {
        val frame = FrameCodec.readFrame(input, maxFrameBytes) ?: return null
        return decode(frame)
    }

    public fun decode(frame: ByteArray): Envelope = try {
        Envelope.ADAPTER.decode(frame)
    } catch (e: IOException) {
        throw ProtocolException("malformed envelope", e)
    } catch (e: RuntimeException) {
        // Wire throws IllegalArgumentException when e.g. two oneof cases are set.
        throw ProtocolException("malformed envelope", e)
    }

    /** Reads the next envelope or fails: used during handshakes where EOF is an error. */
    public fun require(input: InputStream, maxFrameBytes: Int, what: String): Envelope =
        read(input, maxFrameBytes) ?: throw ProtocolException("connection closed while waiting for $what")
}
