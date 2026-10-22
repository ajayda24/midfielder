package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.PowerManager
import android.os.SystemClock
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.discovery.Backoff
import dev.midfielder.core.discovery.Knock
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.TrustStore
import dev.midfielder.proto.DeviceKind
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withTimeoutOrNull
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.cancellation.CancellationException

/**
 * Keeps the phone connected to its paired PC(s) without polling.
 *
 * A connection attempt happens only when something changed: service start, Wi-Fi came up, screen turned
 * on, the PC knocked, or the session dropped. An attempt tries the last known address, then a short mDNS
 * burst (multicast lock held only for that burst). Failed attempts retry with capped backoff only while the
 * screen is on; with the screen off the phone stays silent until the next event.
 */
class ConnectionManager(
    context: Context,
    private val node: MidfielderNode,
    private val trust: TrustStore,
    private val scope: CoroutineScope,
) {
    private val app = context.applicationContext
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    private val power = app.getSystemService(PowerManager::class.java)
    private val wifi = app.getSystemService(WifiManager::class.java)
    private val nsd = app.getSystemService(NsdManager::class.java)
    private val kicks = Channel<Hint>(Channel.CONFLATED)
    private val backoff = Backoff(initialMs = 2_000, maxMs = 5 * 60_000)
    private var retryJob: Job? = null
    @Volatile
    private var knockSocket: DatagramSocket? = null

    @Volatile
    private var stopped = false

    /** When each PC's current session started (elapsed ms), to tell a stable session from one that drops at once. */
    private val connectedAt = ConcurrentHashMap<String, Long>()

    /** When a knock-triggered dial to each PC last failed: knocks don't trigger more than one failed dial a minute. */
    private val knockFailedAt = ConcurrentHashMap<String, Long>()

    @Volatile
    private var screenOn = power.isInteractive

    @Volatile
    private var onLan = false

    /** The Wi-Fi/Ethernet network we dial over (null: none, e.g. this phone is the hotspot; the default route then). */
    @Volatile
    private var lan: Network? = null

    /** Sockets bound to the LAN, so a VPN or "mobile data as default" doesn't carry them off the local network. */
    fun socketFactory(): javax.net.SocketFactory = lan?.socketFactory ?: javax.net.SocketFactory.getDefault()

    /** Called with true on screen-on (the service probes sessions then). */
    var onScreenChanged: (Boolean) -> Unit = {}

    private data class Hint(val reason: String, val address: HostPort? = null, val deviceId: String? = null)

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
            val lan = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
            val changed = lan && !onLan
            onLan = lan
            if (lan) this@ConnectionManager.lan = network
            if (changed) {
                probeAll() // a session from before the switch may be half-open
                kick("network")
            }
        }

        override fun onLost(network: Network) {
            if (lan != null && network != lan) return // another network went; ours is still up
            lan = null
            onLan = false
            // Sessions over the lost network are dead but may not notice for a minute: check them now.
            probeAll()
        }
    }

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            screenOn = intent.action == Intent.ACTION_SCREEN_ON
            onScreenChanged(screenOn)
            if (screenOn) kick("screen on") else retryJob?.cancel()
        }
    }

    fun start() {
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET)
            .build()
        connectivity.registerNetworkCallback(request, networkCallback)
        app.registerReceiver(
            screenReceiver,
            IntentFilter().apply {
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(Intent.ACTION_SCREEN_OFF)
            },
        )
        scope.launch { knockLoop() }
        scope.launch {
            for (hint in kicks) {
                // One bad attempt (an odd address, a Wi-Fi API throwing) must not end the loop: nothing would reconnect.
                try {
                    attempt(hint)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    EventLog.error("Connection attempt failed", e)
                }
            }
        }
        kick("start")
    }

    /** The node connected to [device]. */
    fun onConnected(device: PairedDevice) {
        connectedAt[device.deviceId] = SystemClock.elapsedRealtime()
    }

    /**
     * The session to [device] ended. After a stable session, redial right away; after one that dropped within
     * [STABLE_MS] (the PC accepts then closes, a flaky network), wait with backoff instead of looping.
     */
    fun onDisconnected(device: PairedDevice) {
        val since = connectedAt.remove(device.deviceId)
        val stable = since == null || SystemClock.elapsedRealtime() - since >= STABLE_MS
        if (stable) {
            synchronized(backoff) { backoff.reset() }
            kick("disconnected")
        } else {
            EventLog.add("${device.name} dropped right after connecting; retrying later")
            scheduleRetry()
        }
    }

    private fun probeAll() {
        for (d in node.connectedPeers.value) node.session(d.fingerprint)?.probe()
    }

    fun stop() {
        stopped = true
        runCatching { connectivity.unregisterNetworkCallback(networkCallback) }
        runCatching { app.unregisterReceiver(screenReceiver) }
        knockSocket?.close()
        kicks.close()
        retryJob?.cancel()
    }

    fun kick(reason: String) {
        kicks.trySend(Hint(reason))
    }

    private suspend fun attempt(hint: Hint) {
        val pcs = trust.all().filter { it.kind != DeviceKind.DEVICE_KIND_ANDROID }
            .filter { hint.deviceId == null || it.deviceId == hint.deviceId }
        if (hint.reason == "knock") {
            // The PC knocks when it (re)starts or its address changed; if we think we're still connected, that
            // session is probably half-open: check it now (a failed probe closes it and we redial).
            for (d in pcs) node.session(d.fingerprint)?.probe()
        }
        // No Wi-Fi or Ethernet: a dial can only go over mobile data, where the PC isn't. Wait for the network
        // callback instead, unless this phone is the hotspot (or tethered) the PC is on, or the PC just knocked.
        if (!onLan && hint.reason != "knock" && !hostingLocalNetwork()) {
            EventLog.add("Not on Wi-Fi: waiting for a network before connecting")
            return
        }
        val targets = pcs.filter { node.session(it.fingerprint) == null }
            .filterNot { hint.reason == "knock" && knockedRecently(it.deviceId) }
        if (targets.isEmpty()) return
        var allConnected = true
        for (device in targets) {
            val ok = connect(device, hint.address)
            if (!ok) {
                allConnected = false
                if (hint.reason == "knock") knockFailedAt[device.deviceId] = SystemClock.elapsedRealtime()
            }
        }
        // Backoff resets only once a session proves stable (onDisconnected), not on every successful dial.
        if (allConnected) retryJob?.cancel() else scheduleRetry()
    }

    private fun knockedRecently(deviceId: String): Boolean =
        knockFailedAt[deviceId]?.let { SystemClock.elapsedRealtime() - it < KNOCK_RETRY_MS } == true

    /** Retries later, only while the screen is on (with it off the phone waits for the next event). */
    private fun scheduleRetry() {
        if (!screenOn || stopped || retryJob?.isActive == true) return
        val wait = synchronized(backoff) { backoff.nextDelayMs() }
        retryJob = scope.launch {
            delay(wait)
            kick("retry")
        }
    }

    /**
     * The knock's address first, then the last address. Without a knock, the mDNS search runs alongside the
     * last-address dial, so a PC whose address changed is found in one burst instead of after a dial timeout.
     */
    private suspend fun connect(device: PairedDevice, hinted: HostPort?): Boolean = coroutineScope {
        val last = device.lastAddress?.let { runCatching { HostPort.parse(it) }.getOrNull() }
        val candidates = listOfNotNull(hinted, last).distinct()
        val search = if (hinted == null) async { discover(device.deviceId) } else null
        for (address in candidates) {
            if (tryConnect(device, address)) {
                search?.cancel()
                return@coroutineScope true
            }
        }
        val found = (search ?: async { discover(device.deviceId) }).await()
        when {
            found == null -> false.also { EventLog.add("${device.name} not found on this network") }
            found in candidates -> false
            else -> tryConnect(device, found)
        }
    }

    /** This phone is a hotspot or tethered: a local interface (not mobile data or a VPN) has a private address. */
    private fun hostingLocalNetwork(): Boolean = runCatching {
        java.net.NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().any { nic ->
            val name = nic.name.lowercase()
            nic.isUp && !nic.isLoopback && !nic.isVirtual &&
                listOf("rmnet", "ccmni", "v4-", "tun", "ppp", "dummy", "ipsec", "clat").none { name.startsWith(it) } &&
                nic.inetAddresses.toList().any { it is java.net.Inet4Address && it.isSiteLocalAddress }
        }
    }.getOrDefault(true)

    private suspend fun tryConnect(device: PairedDevice, address: HostPort): Boolean = runCatching {
        node.connect(device, address)
        trust.put(device.copy(lastAddress = address.toString()))
        true
    }.getOrElse {
        EventLog.add("Could not reach ${device.name} at $address: ${it.message ?: it.javaClass.simpleName}")
        false
    }

    /** One short mDNS burst for the PC whose service instance name is its device id. */
    private suspend fun discover(deviceId: String): HostPort? {
        val lock = wifi.createMulticastLock("midfielder-discovery").apply { setReferenceCounted(false) }
        val result = CompletableDeferred<HostPort?>()
        val resolving = AtomicBoolean(false)
        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                result.complete(null)
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                // Runs on an NSD thread: an exception here would crash the app.
                val address = runCatching {
                    @Suppress("DEPRECATION")
                    val host = info.host?.hostAddress?.substringBefore('%')
                    host?.takeIf { info.port in 1..65535 }?.let { HostPort(it, info.port) }
                }.getOrNull()
                result.complete(address)
            }
        }
        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                result.complete(null)
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
            override fun onServiceLost(info: NsdServiceInfo) = Unit
            override fun onServiceFound(info: NsdServiceInfo) {
                // Resolve once: a second resolve fails with ALREADY_ACTIVE and must not cancel the first.
                if (info.serviceName.equals(deviceId, ignoreCase = true) && resolving.compareAndSet(false, true)) {
                    @Suppress("DEPRECATION")
                    runCatching { nsd.resolveService(info, resolveListener) }
                }
            }
        }
        runCatching { lock.acquire() }
        try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
            return withTimeoutOrNull(DISCOVERY_BURST_MS) { result.await() }
        } catch (e: IllegalArgumentException) {
            EventLog.error("Network discovery refused", e)
            return null
        } finally {
            runCatching { nsd.stopServiceDiscovery(discoveryListener) }
            runCatching { lock.release() }
        }
    }

    /**
     * Blocks in receive() (no CPU use) until the PC knocks; then dials it right away. If the port can't be bound
     * (another app holds it, the network is changing) or the socket breaks, it tries again later instead of
     * giving up for the life of the service.
     */
    private suspend fun knockLoop() {
        var waitMs = 5_000L
        while (!stopped) {
            val socket = runCatching {
                DatagramSocket(null).apply {
                    reuseAddress = true
                    bind(InetSocketAddress(Knock.DEFAULT_PORT))
                }
            }.onFailure { EventLog.add("Knock port busy (${it.message}); trying again in ${waitMs / 1000} s") }.getOrNull()
            if (socket != null) {
                knockSocket = socket
                waitMs = 5_000L
                receiveKnocks(socket)
                socket.close()
            }
            if (stopped) return
            delay(waitMs)
            waitMs = (waitMs * 2).coerceAtMost(5 * 60_000L)
        }
    }

    private suspend fun receiveKnocks(socket: DatagramSocket) {
        val buf = ByteArray(64)
        val packet = DatagramPacket(buf, buf.size)
        try {
            while (!stopped) {
                runInterruptible(Dispatchers.IO) { socket.receive(packet) }
                val knock = Knock.decode(buf, packet.length) ?: continue
                val source = packet.address ?: continue
                // A knock only names an address to dial (the TLS pin still decides who we talk to); accept it
                // only from the local network.
                if (!(source.isSiteLocalAddress || source.isLinkLocalAddress || source.isLoopbackAddress || PairLinks.isPrivate(source))) continue
                val host = source.hostAddress?.substringBefore('%') ?: continue
                kicks.trySend(Hint("knock", HostPort(host, knock.port), knock.deviceId))
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (!stopped) EventLog.add("Knock listener stopped: ${e.message ?: e.javaClass.simpleName}")
        }
    }

    private companion object {
        const val SERVICE_TYPE = "_midfielder._tcp"
        const val DISCOVERY_BURST_MS = 8_000L
        const val STABLE_MS = 45_000L
        const val KNOCK_RETRY_MS = 60_000L
    }
}
