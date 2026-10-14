package dev.midfielder.core.discovery

import dev.midfielder.core.crypto.Hex

/**
 * A tiny UDP datagram the PC sends to the phone's last known address when it has something to deliver
 * and no session is up ("please reconnect"). It carries no secrets; the phone only reacts by dialing the
 * already-paired device over mutually-authenticated TLS, rate limited, so a spoofed knock is harmless.
 *
 * Layout (22 bytes): "MFK1" | 16-byte device id | uint16 big-endian listener port.
 */
public data class Knock(val deviceId: String, val port: Int) {
    init {
        require(Hex.isLowerHex(deviceId, 32)) { "device id must be 32 lowercase hex chars" }
        require(port in 1..65535)
    }

    public fun encode(): ByteArray {
        val out = ByteArray(SIZE)
        MAGIC.copyInto(out)
        Hex.decode(deviceId).copyInto(out, 4)
        out[20] = (port ushr 8).toByte()
        out[21] = port.toByte()
        return out
    }

    public companion object {
        public const val SIZE: Int = 22
        public const val DEFAULT_PORT: Int = 47_811
        private val MAGIC = byteArrayOf('M'.code.toByte(), 'F'.code.toByte(), 'K'.code.toByte(), '1'.code.toByte())

        /** Returns null for anything that isn't a well-formed knock. */
        public fun decode(data: ByteArray, length: Int = data.size): Knock? {
            if (length != SIZE || data.size < SIZE) return null
            for (i in 0 until 4) if (data[i] != MAGIC[i]) return null
            val port = ((data[20].toInt() and 0xFF) shl 8) or (data[21].toInt() and 0xFF)
            if (port == 0) return null
            return Knock(Hex.encode(data.copyOfRange(4, 20)), port)
        }
    }
}
