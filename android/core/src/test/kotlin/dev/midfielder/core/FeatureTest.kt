package dev.midfielder.core

import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.feature.BatteryAlerts
import dev.midfielder.core.feature.Features
import dev.midfielder.core.net.InMemoryTrustStore
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.NodeEvents
import dev.midfielder.core.net.PeerSession
import dev.midfielder.proto.BatteryStatus
import dev.midfielder.proto.DeviceKind
import dev.midfielder.proto.Envelope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.InetAddress

class FeatureTest {
    private val nodes = mutableListOf<MidfielderNode>()

    @AfterEach
    fun tearDown() = nodes.forEach { it.close() }

    private class Recorder : NodeEvents {
        val features = Channel<Envelope>(Channel.UNLIMITED)
        val sessions = Channel<PeerSession>(Channel.UNLIMITED)

        override fun onPeerConnected(session: PeerSession) {
            sessions.trySend(session)
        }

        override fun onFeature(from: PeerSession, env: Envelope) {
            features.trySend(env)
        }
    }

    private fun node(name: String, kind: DeviceKind, events: Recorder) =
        MidfielderNode(DeviceIdentity.generate(), name, kind, InMemoryTrustStore(), events).also { nodes += it }

    private fun <T> await(block: suspend () -> T): T = runBlocking { withTimeout(20_000) { block() } }

    @Test
    fun `features are advertised, updated live and gate sending`() {
        val pcEvents = Recorder()
        val phoneEvents = Recorder()
        val pc = node("pc", DeviceKind.DEVICE_KIND_WINDOWS, pcEvents)
        val phone = node("phone", DeviceKind.DEVICE_KIND_ANDROID, phoneEvents)
        pc.setFeatures(setOf(Features.BATTERY)) // before connecting: goes out in Hello
        val port = pc.startListening(0, InetAddress.getLoopbackAddress())
        val invite = pc.openPairing(listOf(HostPort("127.0.0.1", port)))
        val phoneSide = await { phone.pair(PairingInvite.parse(invite.toUri())) }
        val pcSide = await { pcEvents.sessions.receive() }

        assertTrue(phoneSide.has(Features.BATTERY))
        assertTrue(phoneSide.has("file"))
        assertFalse(phoneSide.has(Features.MEDIA))
        assertFalse(pcSide.has(Features.BATTERY)) // the phone hasn't switched anything on

        // Sending only reaches peers that advertise the capability.
        val battery = Envelope(battery_status = BatteryStatus(level = 64, charging = true, plugged = true))
        assertEquals(1, phone.sendFeature(Features.BATTERY, battery))
        assertEquals(0, phone.sendFeature(Features.MEDIA, Envelope(battery_status = BatteryStatus(level = 1))))
        assertEquals(64, await { pcEvents.features.receive() }.battery_status!!.level)

        // Switching a feature on and off reaches the connected peer.
        phone.setFeatures(setOf(Features.MEDIA, Features.RING))
        val changed = await { pcEvents.features.receive() }.features_changed!!
        assertTrue(Features.MEDIA in changed.capabilities && "file" in changed.capabilities)
        assertTrue(pcSide.has(Features.MEDIA) && pcSide.has(Features.RING))
        pc.setFeatures(emptySet())
        await {
            phoneEvents.features.receive()
            while (phoneSide.has(Features.BATTERY)) delay(10)
        }
        assertEquals(0, phone.sendFeature(Features.BATTERY, battery))
    }

    @Test
    fun `battery alerts fire once and re-arm with hysteresis`() {
        val a = BatteryAlerts(fullAt = 80, lowAt = 15)
        assertNull(a.update(70, charging = true))
        assertEquals(BatteryAlerts.Alert.FULL, a.update(80, charging = true))
        assertNull(a.update(81, charging = true))
        assertNull(a.update(79, charging = true)) // hovering around the threshold: no repeat
        assertNull(a.update(80, charging = true))
        assertNull(a.update(80, charging = false)) // unplugged re-arms
        assertEquals(BatteryAlerts.Alert.FULL, a.update(80, charging = true))

        assertEquals(BatteryAlerts.Alert.LOW, a.update(15, charging = false))
        assertNull(a.update(14, charging = false))
        assertNull(a.update(16, charging = false))
        assertNull(a.update(15, charging = false))
        assertNull(a.update(21, charging = false)) // moved well away: re-armed
        assertEquals(BatteryAlerts.Alert.LOW, a.update(15, charging = false))
    }
}
