package dev.midfielder.core.net

import dev.midfielder.core.crypto.Fingerprint
import dev.midfielder.proto.DeviceKind
import java.util.concurrent.ConcurrentHashMap

public data class PairedDevice(
    val fingerprint: Fingerprint,
    val name: String,
    val kind: DeviceKind,
    /** Last address we reached it at (host:port of its listener), if it is a listener. */
    val lastAddress: String? = null,
) {
    val deviceId: String get() = fingerprint.deviceId
}

/** Pinned peers. Platforms persist this (Android: app-private file; Windows: DPAPI-protected file). */
public interface TrustStore {
    public fun get(fingerprint: Fingerprint): PairedDevice?
    public fun put(device: PairedDevice)
    public fun remove(fingerprint: Fingerprint)
    public fun all(): List<PairedDevice>
    public fun isTrusted(fingerprint: Fingerprint): Boolean = get(fingerprint) != null
}

public open class InMemoryTrustStore : TrustStore {
    private val map = ConcurrentHashMap<Fingerprint, PairedDevice>()
    override fun get(fingerprint: Fingerprint): PairedDevice? = map[fingerprint]
    override fun put(device: PairedDevice) {
        map[device.fingerprint] = device
    }
    override fun remove(fingerprint: Fingerprint) {
        map.remove(fingerprint)
    }
    override fun all(): List<PairedDevice> = map.values.toList()
}
