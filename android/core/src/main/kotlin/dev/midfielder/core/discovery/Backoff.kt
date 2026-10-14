package dev.midfielder.core.discovery

import java.util.concurrent.ThreadLocalRandom

/**
 * Exponential backoff with jitter, capped. Used for reconnects and discovery bursts so an unreachable PC
 * costs a handful of attempts per hour instead of a steady drain.
 */
public class Backoff(
    private val initialMs: Long = 1_000,
    private val maxMs: Long = 5 * 60_000,
    private val factor: Double = 2.0,
    private val jitter: Double = 0.2,
) {
    init {
        require(initialMs > 0 && maxMs >= initialMs && factor >= 1.0 && jitter in 0.0..1.0)
    }

    private var current = initialMs

    public fun nextDelayMs(): Long {
        val base = current
        current = minOf(maxMs, (current * factor).toLong().coerceAtLeast(current))
        val spread = (base * jitter).toLong()
        return if (spread == 0L) base else base + ThreadLocalRandom.current().nextLong(-spread, spread + 1)
    }

    public fun reset() {
        current = initialMs
    }
}
