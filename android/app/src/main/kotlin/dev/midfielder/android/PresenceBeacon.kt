package dev.midfielder.android

import android.Manifest
import android.annotation.SuppressLint
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

/**
 * "Lock my PC when I walk away": a tiny Bluetooth LE beacon in low-power mode (about one packet a second, a few
 * mA at most). It carries only a token that rotates every 15 minutes and that only the paired PC can recognise.
 * Runs only while the feature is on and the PC has sent its key.
 */
class PresenceBeacon(context: Context) {
    private val app = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private val rotate = Runnable { restart() }
    private var key: ByteArray? = null
    private var callback: AdvertiseCallback? = null
    private var stateReceiver: BroadcastReceiver? = null

    /** Why the beacon isn't running, or null when it is (or is off). */
    @Volatile
    var problem: String? = null
        private set

    val isAdvertising: Boolean get() = callback != null && problem == null

    fun start(key: ByteArray) {
        main.post {
            this.key = key
            if (stateReceiver == null) {
                val r = object : BroadcastReceiver() {
                    override fun onReceive(context: Context, intent: Intent) = restart() // Bluetooth turned on or off
                }
                stateReceiver = r
                app.registerReceiver(r, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED))
            }
            restart()
        }
    }

    fun stop() {
        main.post {
            key = null
            main.removeCallbacks(rotate)
            stateReceiver?.let { runCatching { app.unregisterReceiver(it) } }
            stateReceiver = null
            stopAdvertising()
            problem = null
        }
    }

    @SuppressLint("MissingPermission") // checked by canAdvertise()
    private fun restart() {
        main.removeCallbacks(rotate)
        stopAdvertising()
        val k = key ?: return
        if (!canAdvertise(app)) {
            problem = "Allow Nearby devices for Midfielder"
            return
        }
        val adapter = app.getSystemService(BluetoothManager::class.java)?.adapter
        val advertiser = adapter?.takeIf { it.isEnabled }?.bluetoothLeAdvertiser
        if (advertiser == null) {
            problem = "Turn on Bluetooth"
            return
        }
        val now = System.currentTimeMillis()
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .setConnectable(false)
            .setTimeout(0)
            .build()
        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .setIncludeTxPowerLevel(false)
            .addManufacturerData(PresenceToken.COMPANY_ID, PresenceToken.manufacturerData(k, PresenceToken.slot(now)))
            .build()
        val cb = object : AdvertiseCallback() {
            override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
                problem = null
            }

            override fun onStartFailure(errorCode: Int) {
                problem = "Bluetooth couldn't advertise (error $errorCode)"
                EventLog.add("Presence beacon failed to start: $errorCode")
            }
        }
        callback = cb
        problem = null
        runCatching { advertiser.startAdvertising(settings, data, cb) }
            .onFailure { problem = "Bluetooth couldn't advertise"; EventLog.error("Presence beacon", it) }
        // A new token every 15 minutes (a second late, so the PC's clock is surely in the new slot too).
        main.postDelayed(rotate, PresenceToken.msToNextSlot(now) + 1_000)
    }

    @SuppressLint("MissingPermission")
    private fun stopAdvertising() {
        val cb = callback ?: return
        callback = null
        if (!canAdvertise(app)) return
        runCatching { app.getSystemService(BluetoothManager::class.java)?.adapter?.bluetoothLeAdvertiser?.stopAdvertising(cb) }
    }

    companion object {
        /** The runtime permission the beacon needs on Android 12+ ("Nearby devices"). */
        val permissions: List<String> =
            if (Build.VERSION.SDK_INT >= 31) listOf(Manifest.permission.BLUETOOTH_ADVERTISE) else emptyList()

        fun canAdvertise(context: Context): Boolean = permissions.all { CallBridge.granted(context, it) }
    }
}
