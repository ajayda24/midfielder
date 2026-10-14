package dev.midfielder.core.crypto

import java.security.MessageDigest
import java.security.cert.Certificate
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

public object Hashes {
    public fun sha256(data: ByteArray): ByteArray = newSha256().digest(data)

    public fun newSha256(): MessageDigest = MessageDigest.getInstance("SHA-256")

    public fun hmacSha256(key: ByteArray, vararg parts: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        for (p in parts) mac.update(p)
        return mac.doFinal()
    }

    /** Constant-time comparison for secrets and MACs. */
    public fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)
}

public object Hex {
    private val DIGITS = "0123456789abcdef".toCharArray()

    public fun encode(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2] = DIGITS[v ushr 4]
            out[i * 2 + 1] = DIGITS[v and 0x0F]
        }
        return String(out)
    }

    public fun decode(hex: String): ByteArray {
        require(hex.length % 2 == 0) { "odd hex length" }
        val out = ByteArray(hex.length / 2)
        for (i in out.indices) {
            val hi = Character.digit(hex[i * 2], 16)
            val lo = Character.digit(hex[i * 2 + 1], 16)
            require(hi >= 0 && lo >= 0) { "invalid hex" }
            out[i] = ((hi shl 4) or lo).toByte()
        }
        return out
    }

    public fun isLowerHex(s: String, length: Int): Boolean =
        s.length == length && s.all { it in '0'..'9' || it in 'a'..'f' }
}

/** SHA-256 of a DER certificate, lowercase hex. This is what devices pin. */
@JvmInline
public value class Fingerprint(public val hex: String) {
    init {
        require(Hex.isLowerHex(hex, 64)) { "fingerprint must be 64 lowercase hex chars" }
    }

    public val bytes: ByteArray get() = Hex.decode(hex)

    /** Short, stable device id: first 16 bytes of the fingerprint. */
    public val deviceId: String get() = hex.substring(0, 32)

    /** Grouped form for showing to humans, e.g. "3f2a 9c01 …". */
    public fun display(): String = hex.substring(0, 16).chunked(4).joinToString(" ")

    override fun toString(): String = hex

    public companion object {
        public fun of(cert: Certificate): Fingerprint = Fingerprint(Hex.encode(Hashes.sha256(cert.encoded)))
    }
}
