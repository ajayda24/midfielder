package dev.midfielder.core

import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.net.InMemoryTrustStore
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.NodeEvents
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.TransferResult
import dev.midfielder.core.transfer.BytesFileSource
import dev.midfielder.core.transfer.DirectoryFileSink
import dev.midfielder.core.transfer.FileSink
import dev.midfielder.proto.ClipKind
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.TransferOffer
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okio.ByteString.Companion.encodeUtf8
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Assumptions.assumeTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Files
import java.nio.file.Path
import java.util.Random
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * Kotlin "phone" against the real C# "PC" node (windows/Midfielder.Cli) over TLS 1.3.
 * Runs only when -PinteropCli=<path to Midfielder.Cli.dll> is given (CI does this).
 */
class InteropTest {
    @TempDir
    lateinit var dir: Path

    @Test
    fun `kotlin phone talks to csharp pc`() {
        val cli = System.getProperty("midfielder.interop.cli")
        assumeTrue(cli != null, "set -PinteropCli to run")
        val pcDir = Files.createDirectories(dir.resolve("pc"))
        val phoneDir = Files.createDirectories(dir.resolve("phone"))
        val process = ProcessBuilder("dotnet", cli, "listen", "--port", "0", "--dir", pcDir.toString())
            .redirectErrorStream(true).start()
        val lines = LinkedBlockingQueue<String>()
        thread(isDaemon = true) { process.inputStream.bufferedReader().forEachLine { lines.put(it) } }
        fun expect(prefix: String): String {
            while (true) {
                val line = lines.poll(30, TimeUnit.SECONDS) ?: error("timed out waiting for '$prefix'")
                if (line.startsWith(prefix)) return line.removePrefix(prefix).trim()
            }
        }

        val clips = Channel<ClipboardItem>(Channel.UNLIMITED)
        val finished = Channel<TransferResult>(Channel.UNLIMITED)
        val events = object : NodeEvents {
            override suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {
                clips.send(item)
            }

            override suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink> =
                offer.files.mapIndexed { i, f -> DirectoryFileSink(phoneDir, "${offer.transfer_id}-$i", f.name) }

            override fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {
                finished.trySend(result)
            }
        }
        val phone = MidfielderNode(DeviceIdentity.generate(), "kotlin-phone", DeviceKind.DEVICE_KIND_ANDROID, InMemoryTrustStore(), events)
        try {
            val invite = PairingInvite.parse(expect("INVITE"))
            val session = runBlocking { withTimeout(20_000) { phone.pair(invite) } }
            assertEquals("cli-pc", session.peer.name)
            assertEquals("kotlin-phone", expect("PAIRED"))

            // Clipboard: phone -> PC, PC echoes back.
            val text = "héllo from kotlin 👋"
            val bytes = text.encodeUtf8()
            phone.broadcastClipboard(
                ClipboardItem(kind = ClipKind.CLIP_KIND_TEXT, mime = "text/plain", inline_data = bytes, size = bytes.size.toLong(), content_sha256 = bytes.sha256()),
            )
            assertEquals(text, expect("CLIP"))
            assertEquals("echo: $text", runBlocking { withTimeout(20_000) { clips.receive() } }.inline_data.utf8())

            // Files: phone -> PC (dialer sends), then PC sends it back (listener sends).
            val payload = ByteArray(1_500_000).also { Random(7).nextBytes(it) }
            val up = runBlocking { withTimeout(30_000) { phone.sendFiles(session.peer.fingerprint, listOf(BytesFileSource("interop.bin", payload, "x"))) } }
            assertTrue(up is TransferResult.Completed, "$up")
            assertArrayEquals(payload, Files.readAllBytes(Path.of(expect("RECEIVED"))))
            val back = runBlocking { withTimeout(30_000) { finished.receive() } } as TransferResult.Completed
            assertEquals("back-interop.bin", Path.of(back.locations.single()).fileName.toString())
            assertArrayEquals(payload, Files.readAllBytes(Path.of(back.locations.single())))
        } finally {
            phone.close()
            process.outputStream.close()
            if (!process.waitFor(5, TimeUnit.SECONDS)) process.destroyForcibly()
        }
    }
}
