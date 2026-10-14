package dev.midfielder.core

import dev.midfielder.core.clip.ClipContent
import dev.midfielder.core.clip.ClipResult
import dev.midfielder.core.clip.ClipSync
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
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.TransferOffer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
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

        val applied = Channel<ClipContent>(Channel.UNLIMITED)
        lateinit var phoneClip: ClipSync
        val finished = Channel<TransferResult>(Channel.UNLIMITED)
        val events = object : NodeEvents {
            override suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {
                phoneClip.onRemote(item)
            }

            override suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink> =
                offer.files.mapIndexed { i, f -> DirectoryFileSink(phoneDir, "${offer.transfer_id}-$i", f.name) }

            override fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {
                finished.trySend(result)
            }
        }
        val phoneIdentity = DeviceIdentity.generate()
        val phone = MidfielderNode(phoneIdentity, "kotlin-phone", DeviceKind.DEVICE_KIND_ANDROID, InMemoryTrustStore(), events)
        val clipScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        phoneClip = ClipSync(phoneIdentity.deviceId, clipScope, { phone.broadcastClipboard(it) }, { c, _ -> applied.send(c) })
        try {
            val invite = PairingInvite.parse(expect("INVITE"))
            val session = runBlocking { withTimeout(20_000) { phone.pair(invite) } }
            assertEquals("cli-pc", session.peer.name)
            assertEquals("kotlin-phone", expect("PAIRED"))

            // Clipboard through the real ClipSync on both sides: phone -> PC, then the PC's echo back.
            val text = "héllo from kotlin 👋\r\nsecond line"
            assertEquals(ClipResult.SENT, phoneClip.onLocalChange(ClipContent.Text(text)))
            assertEquals("APPLIED héllo from kotlin 👋\\r\\nsecond line", "APPLIED " + expect("APPLIED"))
            assertEquals("Applied", expect("CLIP_RESULT"))
            val echo = runBlocking { withTimeout(20_000) { applied.receive() } } as ClipContent.Text
            assertEquals("echo: $text", echo.text)

            // A sensitive item (password manager) still syncs, flagged.
            assertEquals(ClipResult.SENT, phoneClip.onLocalChange(ClipContent.Text("hunter2", sensitive = true)))
            assertEquals("hunter2 [sensitive]", expect("APPLIED"))

            // Files: phone -> PC (dialer sends), then PC sends it back (listener sends).
            val payload = ByteArray(1_500_000).also { Random(7).nextBytes(it) }
            val up = runBlocking { withTimeout(30_000) { phone.sendFiles(session.peer.fingerprint, listOf(BytesFileSource("interop.bin", payload, "x"))) } }
            assertTrue(up is TransferResult.Completed, "$up")
            assertArrayEquals(payload, Files.readAllBytes(Path.of(expect("RECEIVED"))))
            val back = runBlocking { withTimeout(30_000) { finished.receive() } } as TransferResult.Completed
            assertEquals("back-interop.bin", Path.of(back.locations.single()).fileName.toString())
            assertArrayEquals(payload, Files.readAllBytes(Path.of(back.locations.single())))
        } finally {
            clipScope.cancel()
            phone.close()
            process.outputStream.close()
            if (!process.waitFor(5, TimeUnit.SECONDS)) process.destroyForcibly()
        }
    }
}
