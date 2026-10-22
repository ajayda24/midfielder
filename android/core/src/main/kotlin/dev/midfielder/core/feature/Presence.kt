package dev.midfielder.core.feature

import dev.midfielder.core.crypto.Hashes
import java.nio.ByteBuffer

/**
 * The rotating token in this phone's Bluetooth LE beacon: only the paired PC (which holds the key) recognises it, and
 * it changes every 15 minutes, so nobody else can follow the phone by it.
 */
public object PresenceToken {
    public const val SLOT_SECONDS: Long = 900
    public const val TOKEN_BYTES: Int = 8

    /** Manufacturer data company id (0xFFFF: reserved for testing, never a real company). */
    public const val COMPANY_ID: Int = 0xFFFF
    public const val VERSION: Byte = 1

    private val LABEL = "midfielder-presence-v1".toByteArray(Charsets.US_ASCII)

    public fun slot(unixMs: Long): Long = unixMs / 1000 / SLOT_SECONDS

    public fun compute(key: ByteArray, slot: Long): ByteArray =
        Hashes.hmacSha256(key, LABEL, ByteBuffer.allocate(8).putLong(slot).array()).copyOf(TOKEN_BYTES)

    /** Tokens that fit one legacy advertisement next to the version byte (one per paired PC). */
    public const val MAX_TOKENS: Int = 2

    /** The beacon's manufacturer data after the company id: version, then one token per key (each PC finds its own). */
    public fun manufacturerData(key: ByteArray, slot: Long): ByteArray = manufacturerData(listOf(key), slot)

    public fun manufacturerData(keys: List<ByteArray>, slot: Long): ByteArray {
        var out = byteArrayOf(VERSION)
        for (k in keys.take(MAX_TOKENS)) out += compute(k, slot)
        return out
    }

    /** Milliseconds until the next slot starts (when the beacon must change). */
    public fun msToNextSlot(unixMs: Long): Long = (slot(unixMs) + 1) * SLOT_SECONDS * 1000 - unixMs
}
