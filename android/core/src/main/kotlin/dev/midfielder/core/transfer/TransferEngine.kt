package dev.midfielder.core.transfer

import dev.midfielder.core.Limits
import dev.midfielder.core.ProtocolException
import dev.midfielder.core.crypto.Hashes
import dev.midfielder.core.wire.Envelopes
import dev.midfielder.proto.DataDone
import dev.midfielder.proto.DataResume
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.FileEntry
import okio.ByteString.Companion.toByteString
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

public class TransferFailedException(message: String) : java.io.IOException(message)

/**
 * Streams one file over an already-open data connection (see docs/PROTOCOL.md, "Data connection").
 *
 *  receiver -> DataResume(offset)
 *  sender   -> raw bytes [offset, size)
 *  sender   -> DataDone(ok, sha256 of the whole file)
 *  receiver -> DataDone(ok) after verifying
 *
 * Memory use is one fixed [chunkBytes] buffer per call, whatever the file size.
 */
public object TransferEngine {
    public const val DEFAULT_CHUNK_BYTES: Int = 64 * 1024
    private const val PROGRESS_STEP_BYTES = 1024L * 1024

    public fun interface Progress {
        public fun onProgress(transferred: Long, total: Long)
    }

    public fun send(
        input: InputStream,
        output: OutputStream,
        source: FileSource,
        entry: FileEntry,
        progress: Progress = Progress { _, _ -> },
        chunkBytes: Int = DEFAULT_CHUNK_BYTES,
    ) {
        val resume = Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "DataResume").data_resume
            ?: throw ProtocolException("expected DataResume")
        val size = entry.size
        val offset = resume.offset
        if (offset < 0 || offset > size) throw ProtocolException("resume offset $offset outside 0..$size")

        val digest = Hashes.newSha256()
        val buf = ByteArray(chunkBytes)
        source.open().use { src ->
            // Hash the prefix the receiver already has (we don't send it again).
            copyExactly(src, null, offset, digest, buf) { }
            copyExactly(src, output, size - offset, digest, buf) { done ->
                progress.onProgress(Math.addExact(offset, done), size)
            }
        }
        val hash = digest.digest()
        Envelopes.write(
            output, Envelope(data_done = DataDone(ok = true, sha256 = hash.toByteString())),
            Limits.MAX_HANDSHAKE_FRAME_BYTES,
        )
        output.flush()

        val verdict = Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "DataDone").data_done
            ?: throw ProtocolException("expected DataDone")
        if (!verdict.ok) throw TransferFailedException("receiver rejected file: ${verdict.reason.take(200)}")
    }

    /** Returns the committed location of the file. */
    public fun receive(
        input: InputStream,
        output: OutputStream,
        sink: FileSink,
        entry: FileEntry,
        progress: Progress = Progress { _, _ -> },
        chunkBytes: Int = DEFAULT_CHUNK_BYTES,
    ): String {
        val size = entry.size
        if (size < 0) throw ProtocolException("negative file size")
        var offset = sink.partialLength()
        if (offset < 0 || offset > size) {
            sink.discard()
            offset = 0
        }
        Envelopes.write(output, Envelope(data_resume = DataResume(offset = offset)), Limits.MAX_HANDSHAKE_FRAME_BYTES)
        output.flush()

        val digest = Hashes.newSha256()
        val buf = ByteArray(chunkBytes)
        if (offset > 0) sink.openPartial().use { copyExactly(it, null, offset, digest, buf) { } }
        sink.openAt(offset).use { out ->
            copyExactly(input, out, size - offset, digest, buf) { done ->
                progress.onProgress(Math.addExact(offset, done), size)
            }
        }

        val trailer = Envelopes.require(input, Limits.MAX_HANDSHAKE_FRAME_BYTES, "DataDone").data_done
            ?: throw ProtocolException("expected DataDone")
        val actual = digest.digest()
        val matchesTrailer = trailer.ok && Hashes.constantTimeEquals(actual, trailer.sha256.toByteArray())
        val matchesOffer = entry.sha256.size == 0 || Hashes.constantTimeEquals(actual, entry.sha256.toByteArray())
        if (!matchesTrailer || !matchesOffer) {
            sink.discard()
            reply(output, ok = false, reason = "hash mismatch")
            throw TransferFailedException("hash mismatch for ${entry.name}")
        }
        val location = sink.commit()
        reply(output, ok = true, reason = "")
        return location
    }

    private fun reply(output: OutputStream, ok: Boolean, reason: String) {
        Envelopes.write(
            output, Envelope(data_done = DataDone(ok = ok, reason = reason)), Limits.MAX_HANDSHAKE_FRAME_BYTES,
        )
        output.flush()
    }

    /**
     * Copies exactly [count] bytes from [from] to [to] (or just hashes them when [to] is null).
     * Reports progress roughly every MiB so callbacks never dominate CPU time.
     */
    private inline fun copyExactly(
        from: InputStream,
        to: OutputStream?,
        count: Long,
        digest: MessageDigest,
        buf: ByteArray,
        onProgress: (Long) -> Unit,
    ) {
        var remaining = count
        var done = 0L
        var lastReported = 0L
        while (remaining > 0) {
            val want = if (remaining < buf.size) remaining.toInt() else buf.size
            val r = from.read(buf, 0, want)
            if (r < 0) throw EOFException("stream ended with $remaining bytes left")
            digest.update(buf, 0, r)
            to?.write(buf, 0, r)
            remaining -= r
            done += r
            if (done - lastReported >= PROGRESS_STEP_BYTES || remaining == 0L) {
                lastReported = done
                onProgress(done)
            }
        }
    }
}
