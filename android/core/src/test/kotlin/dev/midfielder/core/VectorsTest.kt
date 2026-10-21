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
    fun `feature frames`() {
        val battery = Envelope(battery_status = dev.midfielder.proto.BatteryStatus(level = 64, charging = true, plugged = true))
        assertEquals(v("frame.battery"), frameHex(battery))
        val action = Envelope(
            notification_action = dev.midfielder.proto.NotificationAction(key = "0|com.whatsapp|1|null|10001", reply_text = "On my way 👍"),
        )
        assertEquals(v("frame.notification_action"), frameHex(action))
        assertEquals(action, Envelopes.read(ByteArrayInputStream(Hex.decode(v("frame.notification_action"))), 1024))
        val seek = Envelope(
            media_command = dev.midfielder.proto.MediaCommand(action = dev.midfielder.proto.MediaAction.MEDIA_ACTION_SEEK, seek_ms = 74_000),
        )
        assertEquals(v("frame.media_command"), frameHex(seek))
        val offer = Envelope(audio_offer = dev.midfielder.proto.AudioOffer("00112233445566778899aabbccddeeff", 48_000, 2))
        assertEquals(v("frame.audio_offer"), frameHex(offer))
        val chunk = Envelope(audio_chunk = dev.midfielder.proto.AudioChunk(7, byteArrayOf(0, 1, -1, 0x7f).toByteString()))
        assertEquals(v("frame.audio_chunk"), frameHex(chunk))
        val call = Envelope(
            call_state = dev.midfielder.proto.CallState(
                call_id = 3, phase = dev.midfielder.proto.CallPhase.CALL_PHASE_RINGING,
                number = "+919876543210", contact_name = "Mom", can_answer = true,
            ),
        )
        assertEquals(v("frame.call_state"), frameHex(call))
        val command = Envelope(
            call_command = dev.midfielder.proto.CallCommand(
                call_id = 3, action = dev.midfielder.proto.CallAction.CALL_ACTION_SMS_REPLY, text = "Driving, call you soon",
            ),
        )
        assertEquals(v("frame.call_command"), frameHex(command))
        val key = Envelope(presence_key = dev.midfielder.proto.PresenceKey(key = Hex.decode(v("presence.key")).toByteString()))
        assertEquals(v("frame.presence_key"), frameHex(key))
    }

    @Test
    fun `presence token`() {
        val token = dev.midfielder.core.feature.PresenceToken.compute(Hex.decode(v("presence.key")), v("presence.slot").toLong())
        assertEquals(v("presence.token"), Hex.encode(token))
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
