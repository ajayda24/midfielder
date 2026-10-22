package dev.midfielder.android

import android.Manifest
import android.annotation.SuppressLint
import android.app.AlarmManager
import android.app.PendingIntent
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Handler
import android.os.Looper
import dev.midfielder.core.feature.PresenceToken
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * "Lock my PC when I walk away": a tiny Bluetooth LE beacon in low-power mode (about one packet a second, a few
 * mA at most). It carries only tokens that rotate every 15 minutes and that only the paired PCs can recognise
 * (one token per PC, up to [PresenceToken.MAX_TOKENS]). Runs only while the feature is on and a PC has sent its key.
 *
 * The token changes on an alarm that also fires in Doze: a Handler stops counting while the CPU sleeps, so a phone
 * resting on the desk would keep an old token and the PC would lock.
 */
class PresenceBeacon(context: Context) {
    private val app = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private var keys: List<ByteArray> = emptyList()
    private var advertisedSlot = -1L
    private var callback: AdvertiseCallback? = null
    private var receiver: BroadcastReceiver? = null

    private val _problem = MutableStateFlow<String?>(null)

    /** Why the beacon isn't running (null when it is, or when it's off). The Settings card shows it live. */
    val problem: StateFlow<String?> get() = _problem

    /** Starts or updates the beacon; does nothing if it already advertises these keys for the current slot. */
    fun start(keys: List<ByteArray>) {
        main.post {
            val same = keys.size == this.keys.size && keys.zip(this.keys).all { (a, b) -> a.contentEquals(b) }
            this.keys = keys.take(PresenceToken.MAX_TOKENS)
            if (keys.isEmpty()) return@post stopNow()
            if (keys.size > PresenceToken.MAX_TOKENS) EventLog.add("Auto-lock: only the first ${PresenceToken.MAX_TOKENS} PCs fit in the beacon")
            watchState()
            if (same && callback != null && advertisedSlot == PresenceToken.slot(System.currentTimeMillis())) return@post
            restart()
        }
    }

    fun stop() {
        main.post { stopNow() }
    }

    /** The rotation alarm, screen-on or a clock change: advertise the current slot's tokens if they changed. */
    fun rotate() {
        main.post {
            if (keys.isEmpty()) return@post
            if (callback == null || advertisedSlot != PresenceToken.slot(System.currentTimeMillis())) restart()
            else scheduleRotation()
        }
    }

    private fun stopNow() {
        keys = emptyList()
        cancelRotation()
        receiver?.let { runCatching { app.unregisterReceiver(it) } }
        receiver = null
        stopAdvertising()
        _problem.value = null
    }

    private fun watchState() {
        if (receiver != null) return
        val r = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                if (intent.action == BluetoothAdapter.ACTION_STATE_CHANGED) restart() else rotate()
            }
        }
        receiver = r
        app.registerReceiver(
            r,
            IntentFilter().apply {
                addAction(BluetoothAdapter.ACTION_STATE_CHANGED)
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(Intent.ACTION_TIME_CHANGED)
                addAction(Intent.ACTION_TIMEZONE_CHANGED)
            },
        )
    }

    @SuppressLint("MissingPermission") // checked by canAdvertise()
    private fun restart() {
        stopAdvertising()
        if (keys.isEmpty()) return
        if (!canAdvertise(app)) {
            _problem.value = "Allow Nearby devices for Midfielder"
            return
        }
        val adapter = app.getSystemService(BluetoothManager::class.java)?.adapter
        val advertiser = adapter?.takeIf { it.isEnabled }?.bluetoothLeAdvertiser
        if (advertiser == null) {
            _problem.value = "Turn on Bluetooth"
            return
        }
        val now = System.currentTimeMillis()
        val slot = PresenceToken.slot(now)
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .setConnectable(false)
            .setTimeout(0)
            .build()
        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .setIncludeTxPowerLevel(false)
            .addManufacturerData(PresenceToken.COMPANY_ID, PresenceToken.manufacturerData(keys, slot))
            .build()
        val cb = object : AdvertiseCallback() {
            override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
                _problem.value = null
            }

            override fun onStartFailure(errorCode: Int) {
                if (errorCode == ADVERTISE_FAILED_ALREADY_STARTED) return
                _problem.value = when (errorCode) {
                    ADVERTISE_FAILED_TOO_MANY_ADVERTISERS -> "Bluetooth is busy with other apps; Midfielder will retry"
                    ADVERTISE_FAILED_FEATURE_UNSUPPORTED -> "This phone can't send the Bluetooth signal"
                    else -> "Bluetooth couldn't send the signal (error $errorCode)"
                }
                EventLog.add("Presence beacon failed to start: $errorCode")
                if (errorCode == ADVERTISE_FAILED_TOO_MANY_ADVERTISERS || errorCode == ADVERTISE_FAILED_INTERNAL_ERROR) {
                    main.postDelayed({ if (callback === this) restart() }, RETRY_MS)
                }
            }
        }
        callback = cb
        advertisedSlot = slot
        runCatching { advertiser.startAdvertising(settings, data, cb) }
            .onFailure {
                _problem.value = "Bluetooth couldn't send the signal"
                EventLog.error("Presence beacon", it)
            }
        scheduleRotation()
    }

    @SuppressLint("MissingPermission")
    private fun stopAdvertising() {
        val cb = callback ?: return
        callback = null
        advertisedSlot = -1
        if (!canAdvertise(app)) return
        runCatching { app.getSystemService(BluetoothManager::class.java)?.adapter?.bluetoothLeAdvertiser?.stopAdvertising(cb) }
    }

    /** A second after the next slot starts (so the PC's clock is surely in it too); allowed to fire in Doze. */
    private fun scheduleRotation() {
        val alarms = app.getSystemService(AlarmManager::class.java) ?: return
        val at = System.currentTimeMillis() + PresenceToken.msToNextSlot(System.currentTimeMillis()) + 1_000
        runCatching { alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, rotationIntent()) }
    }

    private fun cancelRotation() {
        runCatching { app.getSystemService(AlarmManager::class.java)?.cancel(rotationIntent()) }
    }

    private fun rotationIntent(): PendingIntent = PendingIntent.getBroadcast(
        app, 0, Intent(app, PresenceRotateReceiver::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    companion object {
        private const val RETRY_MS = 30_000L

        /** The runtime permission the beacon needs on Android 12+ ("Nearby devices"). */
        val permissions: List<String> =
            if (Build.VERSION.SDK_INT >= 31) listOf(Manifest.permission.BLUETOOTH_ADVERTISE) else emptyList()

        fun canAdvertise(context: Context): Boolean = permissions.all { CallBridge.granted(context, it) }
    }
}

/** The beacon's 15-minute token change (an alarm, so it also happens while the phone dozes). */
class PresenceRotateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        SyncEngine.current?.features?.beacon?.rotate()
    }
}
