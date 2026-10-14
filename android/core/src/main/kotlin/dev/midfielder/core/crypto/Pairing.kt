package dev.midfielder.core.crypto

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.util.Base64

public data class HostPort(val host: String, val port: Int) {
    init {
        require(port in 1..65535) { "port out of range" }
        require(host.isNotEmpty() && host.length <= 253) { "bad host" }
    }

    override fun toString(): String = if (host.contains(':')) "[$host]:$port" else "$host:$port"

    public companion object {
        public fun parse(s: String): HostPort {
            val host: String
            val portStr: String
            if (s.startsWith("[")) {
                val end = s.indexOf("]:")
                require(end > 1) { "bad IPv6 address" }
                host = s.substring(1, end)
                portStr = s.substring(end + 2)
            } else {
                val idx = s.lastIndexOf(':')
                require(idx > 0) { "missing port" }
                host = s.substring(0, idx)
                portStr = s.substring(idx + 1)
            }
            val port = portStr.toIntOrNull() ?: throw IllegalArgumentException("bad port")
            return HostPort(host, port)
        }
    }
}

/**
 * What the QR code shown by the listening device (the PC) carries:
 * `midfielder://pair?v=1&fp=<hex>&s=<base64url secret>&a=<host:port>,<host:port>&n=<name>`
 *
 * The secret travels out of band (camera), so a device on the same LAN can't impersonate either side
 * during the first connection.
 */
public class PairingInvite(
    public val fingerprint: Fingerprint,
    public val secret: ByteArray,
    public val addresses: List<HostPort>,
    public val name: String,
) {
    init {
        require(secret.size == SECRET_BYTES) { "secret must be $SECRET_BYTES bytes" }
        require(addresses.size in 1..MAX_ADDRESSES) { "1..$MAX_ADDRESSES addresses required" }
    }

    public fun toUri(): String {
        val enc = { v: String -> URLEncoder.encode(v, StandardCharsets.UTF_8.name()) }
        return "midfielder://pair?v=1" +
            "&fp=${fingerprint.hex}" +
            "&s=${B64.encodeToString(secret)}" +
            "&a=${enc(addresses.joinToString(","))}" +
            "&n=${enc(name)}"
    }

    public companion object {
        public const val SECRET_BYTES: Int = 32
        public const val MAX_ADDRESSES: Int = 8
        private const val MAX_URI_CHARS = 2048
        private val B64 = Base64.getUrlEncoder().withoutPadding()

        public fun newSecret(random: SecureRandom = SecureRandom()): ByteArray =
            ByteArray(SECRET_BYTES).also { random.nextBytes(it) }

        /** Strict parser: anything unexpected throws [IllegalArgumentException]. */
        public fun parse(uri: String): PairingInvite {
            require(uri.length <= MAX_URI_CHARS) { "invite too long" }
            val u = URI(uri)
            require(u.scheme == "midfielder" && u.host == "pair") { "not a midfielder pairing code" }
            val params = (u.rawQuery ?: "").split('&').filter { it.isNotEmpty() }.associate { kv ->
                val i = kv.indexOf('=')
                require(i > 0) { "bad query" }
                kv.substring(0, i) to URLDecoder.decode(kv.substring(i + 1), StandardCharsets.UTF_8.name())
            }
            require(params["v"] == "1") { "unsupported invite version" }
            val fp = Fingerprint(params["fp"] ?: throw IllegalArgumentException("missing fp"))
            val secret = Base64.getUrlDecoder().decode(params["s"] ?: throw IllegalArgumentException("missing s"))
            val addrs = (params["a"] ?: "").split(',').filter { it.isNotEmpty() }.map(HostPort::parse)
            val name = (params["n"] ?: "").take(dev.midfielder.core.Limits.MAX_DEVICE_NAME_CHARS)
            return PairingInvite(fp, secret, addrs, name)
        }
    }
}

/** HMAC proofs that both sides know the invite secret, bound to both certificates (channel binding). */
public object PairingProof {
    private val CLIENT_LABEL = "midfielder-pair-client-v1".toByteArray(StandardCharsets.US_ASCII)
    private val SERVER_LABEL = "midfielder-pair-server-v1".toByteArray(StandardCharsets.US_ASCII)

    public fun client(secret: ByteArray, clientFp: Fingerprint, serverFp: Fingerprint): ByteArray =
        Hashes.hmacSha256(secret, CLIENT_LABEL, clientFp.bytes, serverFp.bytes)

    public fun server(secret: ByteArray, clientFp: Fingerprint, serverFp: Fingerprint): ByteArray =
        Hashes.hmacSha256(secret, SERVER_LABEL, clientFp.bytes, serverFp.bytes)
}
