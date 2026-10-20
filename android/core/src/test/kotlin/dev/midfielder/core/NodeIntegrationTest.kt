package dev.midfielder.core

import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.net.InMemoryTrustStore
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.NodeConfig
import dev.midfielder.core.net.NodeEvents
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.PeerSession
import dev.midfielder.core.net.TransferResult
import dev.midfielder.core.transfer.BytesFileSource
import dev.midfielder.core.transfer.DirectoryFileSink
import dev.midfielder.core.transfer.FileSink
import dev.midfielder.core.transfer.FileSource
import dev.midfielder.proto.ClipKind
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.TransferOffer
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okio.ByteString.Companion.encodeUtf8
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.api.io.TempDir
import java.io.IOException
import java.io.InputStream
import java.net.InetAddress
import java.nio.file.Files
import java.nio.file.Path
import java.util.Random
import java.util.concurrent.atomic.AtomicInteger

class NodeIntegrationTest {
    @TempDir
    lateinit var dir: Path

    private val nodes = mutableListOf<MidfielderNode>()

    @AfterEach
    fun tearDown() {
        nodes.forEach { it.close() }
    }

    private class Recorder(val inbox: Path, var accept: Boolean = true) : NodeEvents {
        val clips = Channel<ClipboardItem>(Channel.UNLIMITED)
        val finished = Channel<TransferResult>(Channel.UNLIMITED)
        val disconnected = Channel<Throwable?>(Channel.UNLIMITED)
        val paired = Channel<PairedDevice>(Channel.UNLIMITED)
        val progress: MutableList<Triple<String, Long, Long>> = java.util.Collections.synchronizedList(ArrayList())

        override fun onTransferProgress(transferId: String, transferred: Long, total: Long) {
            progress += Triple(transferId, transferred, total)
        }

        override fun onPaired(device: PairedDevice) {
            paired.trySend(device)
        }

        override fun onPeerDisconnected(device: PairedDevice, cause: Throwable?) {
            disconnected.trySend(cause)
        }

        override suspend fun onClipboard(from: PairedDevice, item: ClipboardItem) {
            clips.send(item)
        }

        override suspend fun onTransferOffer(from: PairedDevice, offer: TransferOffer): List<FileSink>? =
            if (!accept) null else offer.files.mapIndexed { i, f -> DirectoryFileSink(inbox, "${offer.transfer_id}-$i", f.name) }

        override fun onTransferFinished(transferId: String, from: PairedDevice, result: TransferResult) {
            finished.trySend(result)
        }
    }

    private inner class Device(name: String, kind: DeviceKind, config: NodeConfig = NodeConfig()) {
        val inbox: Path = Files.createDirectories(dir.resolve(name))
        val events = Recorder(inbox)
        val trust = InMemoryTrustStore()
        val node = MidfielderNode(DeviceIdentity.generate(), name, kind, trust, events, config).also { nodes += it }
    }

    private fun pcAndPhone(pcConfig: NodeConfig = NodeConfig(), phoneConfig: NodeConfig = NodeConfig()):
        Triple<Device, Device, HostPort> {
        val pc = Device("pc", DeviceKind.DEVICE_KIND_WINDOWS, pcConfig)
        val port = pc.node.startListening(0, InetAddress.getLoopbackAddress())
        return Triple(pc, Device("phone", DeviceKind.DEVICE_KIND_ANDROID, phoneConfig), HostPort("127.0.0.1", port))
    }

    private fun <T> await(block: suspend () -> T): T = runBlocking { withTimeout(20_000) { block() } }

    private fun random(n: Int) = ByteArray(n).also { Random(n.toLong()).nextBytes(it) }

    @Test
    fun `pairs once with the right code and then trusts both ways`() {
        val (pc, phone, addr) = pcAndPhone()
        val invite = pc.node.openPairing(listOf(addr))
        val session = await { phone.node.pair(PairingInvite.parse(invite.toUri())) }
        assertTrue(session.isOpen)
        assertEquals("pc", session.peer.name)
        assertEquals("phone", await { pc.events.paired.receive() }.name)
        assertTrue(pc.trust.isTrusted(phone.node.fingerprint))
        assertTrue(phone.trust.isTrusted(pc.node.fingerprint))

        // The invite is single-use: a second device can't reuse it.
        val intruder = Device("intruder", DeviceKind.DEVICE_KIND_ANDROID)
        assertThrows<IOException> { await { intruder.node.pair(invite) } }
        assertFalse(pc.trust.isTrusted(intruder.node.fingerprint))
    }

    @Test
    fun `wrong pairing code is rejected and burns the window`() {
        val (pc, phone, addr) = pcAndPhone()
        val invite = pc.node.openPairing(listOf(addr))
        val forged = PairingInvite(invite.fingerprint, PairingInvite.newSecret(), invite.addresses, invite.name)
        assertThrows<IOException> { await { phone.node.pair(forged) } }
        assertTrue(pc.trust.all().isEmpty())
        // Even the right code no longer works: one attempt per window.
        assertThrows<IOException> { await { phone.node.pair(invite) } }
        assertTrue(pc.trust.all().isEmpty())
    }

    @Test
    fun `unpaired devices cannot connect`() {
        val (pc, _, addr) = pcAndPhone()
        val stranger = Device("stranger", DeviceKind.DEVICE_KIND_ANDROID)
        val fakePc = PairedDevice(pc.node.fingerprint, "pc", DeviceKind.DEVICE_KIND_WINDOWS)
        stranger.trust.put(fakePc) // the stranger trusts the PC, but the PC doesn't trust the stranger
        assertThrows<IOException> { await { stranger.node.connect(fakePc, addr) } }
        assertTrue(pc.node.connectedPeers.value.isEmpty())
    }

    @Test
    fun `clipboard items flow both ways`() {
        val (pc, phone, addr) = pcAndPhone()
        await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        await { pc.events.paired.receive() }
        waitUntil { pc.node.connectedPeers.value.size == 1 }

        phone.node.broadcastClipboard(clip("from phone"))
        assertEquals("from phone", await { pc.events.clips.receive() }.inline_data.utf8())
        pc.node.broadcastClipboard(clip("from pc"))
        assertEquals("from pc", await { phone.events.clips.receive() }.inline_data.utf8())
    }

    @Test
    fun `files go phone to pc and pc to phone`() {
        val (pc, phone, addr) = pcAndPhone()
        await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        waitUntil { pc.node.connectedPeers.value.size == 1 }

        val photo = random(2_500_000)
        val up = await {
            phone.node.sendFiles(pc.node.fingerprint, listOf(BytesFileSource("IMG_0001.jpg", photo, "image/jpeg")))
        }
        assertTrue(up is TransferResult.Completed, "$up")
        val received = await { pc.events.finished.receive() } as TransferResult.Completed
        assertArrayEquals(photo, Files.readAllBytes(Path.of(received.locations.single())))

        val doc = random(700_001)
        val notes = "notes".toByteArray()
        val id = pc.node.newTransferId()
        val down = await {
            pc.node.sendFiles(
                phone.node.fingerprint,
                listOf(BytesFileSource("report.pdf", doc, "application/pdf"), BytesFileSource("n.txt", notes, "text/plain")),
                transferId = id,
            )
        }
        assertTrue(down is TransferResult.Completed, "$down")
        val got = await { phone.events.finished.receive() } as TransferResult.Completed
        assertArrayEquals(doc, Files.readAllBytes(Path.of(got.locations[0])))
        assertArrayEquals(notes, Files.readAllBytes(Path.of(got.locations[1])))

        // Progress covers the whole transfer (both files), under the caller's id, and never goes backwards.
        val total = (doc.size + notes.size).toLong()
        for (events in listOf(pc.events.progress, phone.events.progress)) {
            val mine = synchronized(events) { events.filter { it.first == id } }
            assertTrue(mine.isNotEmpty())
            assertTrue(mine.all { it.third == total }, "$mine")
            assertEquals(total, mine.last().second)
            assertTrue(mine.zipWithNext().all { (a, b) -> b.second >= a.second })
        }
    }

    @Test
    fun `declined offers fail cleanly`() {
        val (pc, phone, addr) = pcAndPhone()
        await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        waitUntil { pc.node.connectedPeers.value.size == 1 }
        pc.events.accept = false
        val result = await { phone.node.sendFiles(pc.node.fingerprint, listOf(BytesFileSource("a", ByteArray(5), "x"))) }
        assertTrue(result is TransferResult.Failed)
    }

    @Test
    fun `interrupted transfer resumes instead of restarting`() {
        val (pc, phone, addr) = pcAndPhone()
        await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        waitUntil { pc.node.connectedPeers.value.size == 1 }

        val video = random(3_000_000)
        val source = FlakySource("clip.mp4", video, failAfter = 1_200_000)
        val result = await { phone.node.sendFiles(pc.node.fingerprint, listOf(source)) }
        assertTrue(result is TransferResult.Completed, "$result")
        val received = await { pc.events.finished.receive() } as TransferResult.Completed
        assertArrayEquals(video, Files.readAllBytes(Path.of(received.locations.single())))
        assertEquals(2, source.opens.get())
        // Only the first attempt's bytes plus the remainder were sent: total read < 2x the file.
        assertTrue(source.bytesServed.get() < 2L * video.size)
    }

    @Test
    fun `silent peer is dropped by the listener`() {
        val (pc, phone, addr) = pcAndPhone(
            pcConfig = NodeConfig(listenerIdleTimeoutMs = 600),
            phoneConfig = NodeConfig(heartbeatIntervalMs = 60_000),
        )
        await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        await { phone.events.disconnected.receive() }
        waitUntil { pc.node.connectedPeers.value.isEmpty() && phone.node.connectedPeers.value.isEmpty() }
    }

    @Test
    fun `heartbeat keeps an idle session alive`() {
        val (pc, phone, addr) = pcAndPhone(
            pcConfig = NodeConfig(listenerIdleTimeoutMs = 1_500),
            phoneConfig = NodeConfig(heartbeatIntervalMs = 300, heartbeatTimeoutMs = 300),
        )
        val session = await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        Thread.sleep(3_000)
        assertTrue(session.isOpen)
        assertNull(phone.events.disconnected.tryReceive().getOrNull())
    }

    @Test
    fun `reconnects without pairing again and unpair cuts it off`() {
        val (pc, phone, addr) = pcAndPhone()
        val first = await { phone.node.pair(pc.node.openPairing(listOf(addr))) }
        val pcDevice = first.peer
        first.close()
        waitUntil { pc.node.connectedPeers.value.isEmpty() }

        val second: PeerSession = await { phone.node.connect(pcDevice, addr) }
        assertTrue(second.isOpen)
        waitUntil { pc.node.connectedPeers.value.size == 1 }

        pc.node.unpair(phone.node.fingerprint)
        waitUntil { !second.isOpen }
        assertThrows<IOException> { await { phone.node.connect(pcDevice, addr) } }
    }

    private fun clip(text: String): ClipboardItem {
        val bytes = text.encodeUtf8()
        return ClipboardItem(
            kind = ClipKind.CLIP_KIND_TEXT, mime = "text/plain", inline_data = bytes, size = bytes.size.toLong(),
            content_sha256 = bytes.sha256(),
        )
    }

    private fun waitUntil(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 10_000
        while (!condition()) {
            check(System.currentTimeMillis() < deadline) { "condition not met in time" }
            Thread.sleep(20)
        }
    }

    /** Fails mid-stream on the first open, like a phone walking out of Wi-Fi range. */
    private class FlakySource(override val name: String, private val bytes: ByteArray, private val failAfter: Int) :
        FileSource {
        val opens = AtomicInteger()
        val bytesServed = java.util.concurrent.atomic.AtomicLong()
        override val size: Long = bytes.size.toLong()
        override val mime: String = "video/mp4"
        override val modifiedMs: Long = 0

        override fun open(): InputStream {
            val attempt = opens.incrementAndGet()
            return object : InputStream() {
                var pos = 0
                override fun read(): Int = throw UnsupportedOperationException()
                override fun read(b: ByteArray, off: Int, len: Int): Int {
                    if (attempt == 1 && pos >= failAfter) throw IOException("wifi dropped")
                    if (pos >= bytes.size) return -1
                    val n = minOf(len, bytes.size - pos, if (attempt == 1) failAfter - pos else Int.MAX_VALUE)
                    System.arraycopy(bytes, pos, b, off, n)
                    pos += n
                    bytesServed.addAndGet(n.toLong())
                    return n
                }
            }
        }
    }
}
