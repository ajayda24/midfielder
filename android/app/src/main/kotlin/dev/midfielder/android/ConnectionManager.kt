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
import dev.midfielder.core.crypto.HostPort
import dev.midfielder.core.discovery.Backoff
import dev.midfielder.core.discovery.Knock
import dev.midfielder.core.net.MidfielderNode
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.TrustStore
import dev.midfielder.proto.DeviceKind
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
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
    private var knockSocket: DatagramSocket? = null

    @Volatile
    private var screenOn = power.isInteractive

    @Volatile
    private var onLan = false

    /** Called with true on screen-on (the service probes sessions then). */
    var onScreenChanged: (Boolean) -> Unit = {}

    private data class Hint(val reason: String, val address: HostPort? = null, val deviceId: String? = null)

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
            val lan = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
            val changed = lan && !onLan
            onLan = lan
            if (changed) kick("network")
        }

        override fun onLost(network: Network) {
            onLan = false
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
            for (hint in kicks) attempt(hint)
        }
        kick("start")
    }

    fun stop() {
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
        val targets = trust.all().filter { it.kind != DeviceKind.DEVICE_KIND_ANDROID && node.session(it.fingerprint) == null }
            .filter { hint.deviceId == null || it.deviceId == hint.deviceId }
        if (targets.isEmpty()) return
        var allConnected = true
        for (device in targets) {
            if (!connect(device, hint.address)) allConnected = false
        }
        if (allConnected) {
            backoff.reset()
            retryJob?.cancel()
        } else if (screenOn && (retryJob?.isActive != true)) {
            val wait = backoff.nextDelayMs()
            retryJob = scope.launch {
                delay(wait)
                kick("retry")
            }
        }
    }

    private suspend fun connect(device: PairedDevice, hinted: HostPort?): Boolean {
        val candidates = listOfNotNull(hinted, device.lastAddress?.let { runCatching { HostPort.parse(it) }.getOrNull() }).distinct()
        for (address in candidates) {
            if (tryConnect(device, address)) return true
        }
        val found = discover(device.deviceId) ?: return false
        return tryConnect(device, found)
    }

    private suspend fun tryConnect(device: PairedDevice, address: HostPort): Boolean = runCatching {
        node.connect(device, address)
        trust.put(device.copy(lastAddress = address.toString()))
        true
    }.getOrDefault(false)

    /** One short mDNS burst for the PC whose service instance name is its device id. */
    private suspend fun discover(deviceId: String): HostPort? {
        val lock = wifi.createMulticastLock("midfielder-discovery").apply { setReferenceCounted(false) }
        val result = CompletableDeferred<HostPort?>()
        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                result.complete(null)
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                @Suppress("DEPRECATION")
                val host = info.host?.hostAddress
                result.complete(host?.let { HostPort(it, info.port) })
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
                if (info.serviceName.equals(deviceId, ignoreCase = true)) {
                    @Suppress("DEPRECATION")
                    runCatching { nsd.resolveService(info, resolveListener) }
                }
            }
        }
        lock.acquire()
        try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
            return withTimeoutOrNull(DISCOVERY_BURST_MS) { result.await() }
        } finally {
            runCatching { nsd.stopServiceDiscovery(discoveryListener) }
            lock.release()
        }
    }

    /** Blocks in receive() (no CPU use) until the PC knocks; then dials it right away. */
    private suspend fun knockLoop() {
        val socket = runCatching {
            DatagramSocket(null).apply {
                reuseAddress = true
                bind(InetSocketAddress(Knock.DEFAULT_PORT))
            }
        }.getOrNull() ?: return
        knockSocket = socket
        val buf = ByteArray(64)
        val packet = DatagramPacket(buf, buf.size)
        runCatching {
            while (true) {
                runInterruptible(Dispatchers.IO) { socket.receive(packet) }
                val knock = Knock.decode(buf, packet.length) ?: continue
                val host = packet.address?.hostAddress ?: continue
                kicks.trySend(Hint("knock", HostPort(host, knock.port), knock.deviceId))
            }
        }
        socket.close()
    }

    private companion object {
        const val SERVICE_TYPE = "_midfielder._tcp"
        const val DISCOVERY_BURST_MS = 8_000L
    }
}
