package dev.midfielder.core.net

import dev.midfielder.core.Limits

public data class NodeConfig(
    val maxFrameBytes: Int = Limits.MAX_FRAME_BYTES,
    val connectTimeoutMs: Int = 5_000,
    val handshakeTimeoutMs: Int = 10_000,
    /** Read timeout on data connections; a stalled transfer fails and is retried/resumed. */
    val dataReadTimeoutMs: Int = 30_000,
    /** The dialer pings only after this much silence. Long on purpose: an idle radio is a sleeping radio. */
    val heartbeatIntervalMs: Long = 4 * 60_000L,
    val heartbeatTimeoutMs: Long = 20_000L,
    /**
     * The listener never pings; it drops a session silent this long. Long on purpose: a phone in deep sleep
     * can't ping, and dropping it would only cause a reconnect cycle. Dead sessions are replaced on reconnect.
     */
    val listenerIdleTimeoutMs: Long = 45 * 60_000L,
    val sendQueueCapacity: Int = 64,
    val maxConnections: Int = 16,
    val dataRetryAttempts: Int = 5,
    val answerTimeoutMs: Long = 2 * 60_000L,
    /** How long an interrupted transfer waits for the peer to come back before failing. */
    val resumeWindowMs: Long = 10 * 60_000L,
    /**
     * Monotonic clock in ms. Android passes SystemClock.elapsedRealtime so time spent in deep sleep counts.
     */
    val clock: () -> Long = { System.nanoTime() / 1_000_000 },
)
