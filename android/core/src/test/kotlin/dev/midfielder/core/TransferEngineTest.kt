package dev.midfielder.core

import dev.midfielder.core.crypto.Hashes
import dev.midfielder.core.transfer.BytesFileSource
import dev.midfielder.core.transfer.DirectoryFileSink
import dev.midfielder.core.transfer.TransferEngine
import dev.midfielder.core.transfer.TransferFailedException
import dev.midfielder.proto.FileEntry
import okio.ByteString.Companion.toByteString
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.api.io.TempDir
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.nio.file.Files
import java.nio.file.Path
import java.util.Random
import kotlin.concurrent.thread

class TransferEngineTest {
    @TempDir
    lateinit var dir: Path

    private fun data(n: Int) = ByteArray(n).also { Random(n.toLong()).nextBytes(it) }

    /** Runs sender and receiver against each other through in-memory pipes. */
    private fun transfer(source: BytesFileSource, entry: FileEntry, sink: DirectoryFileSink): String {
        val toReceiver = PipedOutputStream()
        val receiverIn = PipedInputStream(toReceiver, 256 * 1024)
        val toSender = PipedOutputStream()
        val senderIn = PipedInputStream(toSender, 256 * 1024)
        var senderError: Throwable? = null
        val t = thread {
            try {
                TransferEngine.send(senderIn, toReceiver, source, entry)
            } catch (e: Throwable) {
                senderError = e
            } finally {
                toReceiver.close()
            }
        }
        try {
            return TransferEngine.receive(receiverIn, toSender, sink, entry)
        } finally {
            toSender.close()
            t.join()
            senderError?.let { if (it !is TransferFailedException && it !is java.io.IOException) throw it }
        }
    }

    @Test
    fun `transfers a multi-chunk file and commits it`() {
        val bytes = data(1_000_003)
        val entry = FileEntry(name = "big.bin", size = bytes.size.toLong())
        val location = transfer(BytesFileSource("big.bin", bytes, "x"), entry, DirectoryFileSink(dir, "t1-0", "big.bin"))
        assertArrayEquals(bytes, Files.readAllBytes(Path.of(location)))
    }

    @Test
    fun `resumes from partial data`() {
        val bytes = data(300_000)
        val sink = DirectoryFileSink(dir, "t2-0", "resume.bin")
        sink.openAt(0).use { it.write(bytes, 0, 123_456) } // an earlier attempt got this far
        val entry = FileEntry(name = "resume.bin", size = bytes.size.toLong())
        val location = transfer(BytesFileSource("resume.bin", bytes, "x"), entry, sink)
        assertArrayEquals(bytes, Files.readAllBytes(Path.of(location)))
    }

    @Test
    fun `corrupt partial data is detected and discarded`() {
        val bytes = data(50_000)
        val sink = DirectoryFileSink(dir, "t3-0", "c.bin")
        sink.openAt(0).use { it.write(ByteArray(10_000)) } // zeros: not what the sender has
        val entry = FileEntry(name = "c.bin", size = bytes.size.toLong())
        assertThrows<TransferFailedException> { transfer(BytesFileSource("c.bin", bytes, "x"), entry, sink) }
        assertEquals(0L, sink.partialLength())
        // A retry from scratch then succeeds.
        val location = transfer(BytesFileSource("c.bin", bytes, "x"), entry, sink)
        assertArrayEquals(bytes, Files.readAllBytes(Path.of(location)))
    }

    @Test
    fun `offer hash mismatch is rejected`() {
        val bytes = data(1000)
        val entry = FileEntry(name = "h.bin", size = 1000, sha256 = Hashes.sha256(ByteArray(1000)).toByteString())
        val sink = DirectoryFileSink(dir, "t4-0", "h.bin")
        assertThrows<TransferFailedException> { transfer(BytesFileSource("h.bin", bytes, "x"), entry, sink) }
        assertFalse(Files.exists(dir.resolve("h.bin")))
    }

    @Test
    fun `never overwrites an existing file`() {
        Files.write(dir.resolve("same.txt"), byteArrayOf(1))
        val bytes = data(10)
        val entry = FileEntry(name = "same.txt", size = 10)
        val location = transfer(BytesFileSource("same.txt", bytes, "x"), entry, DirectoryFileSink(dir, "t5-0", "same.txt"))
        assertEquals("same (1).txt", Path.of(location).fileName.toString())
        assertArrayEquals(byteArrayOf(1), Files.readAllBytes(dir.resolve("same.txt")))
    }

    @Test
    fun `zero byte file`() {
        val entry = FileEntry(name = "empty", size = 0)
        val location = transfer(BytesFileSource("empty", ByteArray(0), "x"), entry, DirectoryFileSink(dir, "t6-0", "empty"))
        assertEquals(0L, Files.size(Path.of(location)))
    }
}
