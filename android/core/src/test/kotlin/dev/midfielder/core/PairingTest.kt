package dev.midfielder.core

import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.crypto.PairingInvite
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class PairingTest {
    private val identity = DeviceIdentity.generate()

    @Test
    fun `invite round trips through its URI`() {
        val invite = PairingInvite(
            identity.fingerprint, PairingInvite.newSecret(),
            listOf(HostPort("192.168.1.20", 47810), HostPort("fe80::1", 47810)), "Ajay's laptop & co",
        )
        val parsed = PairingInvite.parse(invite.toUri())
        assertEquals(invite.fingerprint, parsed.fingerprint)
        assertArrayEquals(invite.secret, parsed.secret)
        assertEquals(invite.addresses, parsed.addresses)
        assertEquals(invite.name, parsed.name)
    }

    @Test
    fun `malformed invites are rejected`() {
        val good = PairingInvite(identity.fingerprint, PairingInvite.newSecret(), listOf(HostPort("10.0.0.2", 1)), "x")
            .toUri()
        assertThrows<IllegalArgumentException> { PairingInvite.parse("https://evil.example/pair?v=1") }
        assertThrows<IllegalArgumentException> { PairingInvite.parse(good.replace("v=1", "v=9")) }
        assertThrows<IllegalArgumentException> { PairingInvite.parse(good.replace("fp=", "fp=zz")) }
        assertThrows<IllegalArgumentException> { PairingInvite.parse(good + "&a=" + "1.2.3.4%3A99999") }
        assertThrows<IllegalArgumentException> { PairingInvite.parse("midfielder://pair?" + "a".repeat(5000)) }
    }

    @Test
    fun `identity survives pkcs12 round trip`() {
        val pw = "pw".toCharArray()
        val restored = DeviceIdentity.fromPkcs12(identity.toPkcs12(pw), pw)
        assertEquals(identity.fingerprint, restored.fingerprint)
        assertNotEquals(identity.fingerprint, DeviceIdentity.generate().fingerprint)
    }
}
