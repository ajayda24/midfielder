package dev.midfielder.core

import dev.midfielder.core.crypto.Fingerprint
import dev.midfielder.core.crypto.Hex
import dev.midfielder.core.crypto.PairingProof
import dev.midfielder.core.discovery.Knock
import dev.midfielder.core.transfer.FileNameSanitizer
import dev.midfielder.core.wire.Envelopes
import dev.midfielder.proto.ClipKind
import dev.midfielder.proto.ClipboardItem
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.Envelope
import dev.midfielder.proto.Hello
import okio.ByteString.Companion.toByteString
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.Properties

/**
 * Golden vectors shared with the C# test suite (protocol/test-vectors/vectors.properties).
 * If either implementation drifts, both suites fail on the same line.
 */
class VectorsTest {
    private val vectors: Properties by lazy {
        val dir = System.getProperty("midfielder.vectors") ?: "../../protocol/test-vectors"
        Properties().apply { File(dir, "vectors.properties").inputStream().use(::load) }
    }

    private fun v(key: String): String = vectors.getProperty(key) ?: error("missing vector $key")

    private fun frameHex(env: Envelope): String {
        val out = ByteArrayOutputStream()
        Envelopes.write(out, env, Limits.MAX_FRAME_BYTES)
        return Hex.encode(out.toByteArray())
    }

    @Test
    fun `hello frame`() {
        val env = Envelope(
            hello = Hello(
                device_id = "00112233445566778899aabbccddeeff",
                device_name = "Laptop",
                kind = DeviceKind.DEVICE_KIND_WINDOWS,
                protocol_version = 1,
                capabilities = listOf("clipboard.text", "file"),
            ),
        )
        assertEquals(v("frame.hello"), frameHex(env))
        assertEquals(env, Envelopes.read(ByteArrayInputStream(Hex.decode(v("frame.hello"))), 1024))
    }

    @Test
    fun `clipboard frame`() {
        val text = "héllo 👋".toByteArray()
        val env = Envelope(
            clipboard = ClipboardItem(
                id = ByteArray(16) { it.toByte() }.toByteString(),
                origin_device_id = "00112233445566778899aabbccddeeff",
                created_at_ms = 1_700_000_000_000,
                kind = ClipKind.CLIP_KIND_TEXT,
                mime = "text/plain",
                content_sha256 = dev.midfielder.core.crypto.Hashes.sha256(text).toByteString(),
                inline_data = text.toByteString(),
                size = text.size.toLong(),
            ),
        )
        assertEquals(v("frame.clipboard"), frameHex(env))
    }

    @Test
    fun `pairing proofs`() {
        val secret = Hex.decode(v("pair.secret"))
        val client = Fingerprint(v("pair.client_fp"))
        val server = Fingerprint(v("pair.server_fp"))
        assertEquals(v("pair.client_proof"), Hex.encode(PairingProof.client(secret, client, server)))
        assertEquals(v("pair.server_proof"), Hex.encode(PairingProof.server(secret, client, server)))
    }

    @Test
    fun knock() {
        assertEquals(v("knock"), Hex.encode(Knock("00112233445566778899aabbccddeeff", 47811).encode()))
    }

    @Test
    fun `file name sanitizer`() {
        var i = 0
        while (vectors.getProperty("sanitize.$i.in") != null) {
            assertEquals(v("sanitize.$i.out"), FileNameSanitizer.sanitize(v("sanitize.$i.in")), "case $i")
            i++
        }
        check(i > 5)
    }
}
