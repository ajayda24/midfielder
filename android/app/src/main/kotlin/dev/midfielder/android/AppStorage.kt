package dev.midfielder.android

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.Fingerprint
import dev.midfielder.core.net.PairedDevice
import dev.midfielder.core.net.TrustStore
import dev.midfielder.proto.DeviceKind
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * App-private files encrypted with an AES-256-GCM key that never leaves the Android Keystore
 * (hardware-backed where available). Holds the device identity and the paired-device list.
 */
class SecureFiles(context: Context) {
    private val dir = File(context.noBackupFilesDir, "secure").apply { mkdirs() }

    fun read(name: String): ByteArray? {
        val f = File(dir, name)
        if (!f.exists()) return null
        return runCatching {
            val blob = f.readBytes()
            val iv = blob.copyOfRange(0, IV_BYTES)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
            cipher.doFinal(blob, IV_BYTES, blob.size - IV_BYTES)
        }.getOrNull()
    }

    fun write(name: String, data: ByteArray) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val out = cipher.iv + cipher.doFinal(data)
        val tmp = File(dir, "$name.tmp")
        tmp.writeBytes(out)
        if (!tmp.renameTo(File(dir, name))) error("could not save $name")
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "midfielder-storage"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
    }
}

class AppStorage(context: Context) {
    private val files = SecureFiles(context)
    val prefs: SharedPreferences = context.getSharedPreferences("settings", Context.MODE_PRIVATE)

    fun loadOrCreateIdentity(): DeviceIdentity {
        files.read(IDENTITY)?.let { bytes ->
            runCatching { return DeviceIdentity.fromPkcs12(bytes, PKCS12_PASSWORD) }
        }
        val identity = DeviceIdentity.generate()
        files.write(IDENTITY, identity.toPkcs12(PKCS12_PASSWORD))
        return identity
    }

    /** Paired PCs, written through to encrypted storage on every change (rare). */
    val trustStore: TrustStore = object : TrustStore {
        private val map = ConcurrentHashMap<Fingerprint, PairedDevice>().apply {
            files.read(DEVICES)?.let { bytes ->
                runCatching {
                    val arr = JSONArray(String(bytes, Charsets.UTF_8))
                    for (i in 0 until arr.length()) {
                        val o = arr.getJSONObject(i)
                        val fp = Fingerprint(o.getString("fp"))
                        put(fp, PairedDevice(fp, o.getString("name"), DeviceKind.fromValue(o.getInt("kind")) ?: DeviceKind.DEVICE_KIND_OTHER, o.optString("addr").ifEmpty { null }))
                    }
                }
            }
        }

        override fun get(fingerprint: Fingerprint): PairedDevice? = map[fingerprint]
        override fun all(): List<PairedDevice> = map.values.toList()

        override fun put(device: PairedDevice) {
            val previous = map.put(device.fingerprint, device)
            // Merge: a listener-side update (no address) must not erase the address we dialed.
            if (device.lastAddress == null && previous?.lastAddress != null) {
                map[device.fingerprint] = device.copy(lastAddress = previous.lastAddress)
            }
            save()
        }

        override fun remove(fingerprint: Fingerprint) {
            map.remove(fingerprint)
            save()
        }

        @Synchronized
        private fun save() {
            val arr = JSONArray()
            for (d in map.values) {
                arr.put(
                    JSONObject().put("fp", d.fingerprint.hex).put("name", d.name).put("kind", d.kind.value)
                        .put("addr", d.lastAddress ?: ""),
                )
            }
            files.write(DEVICES, arr.toString().toByteArray(Charsets.UTF_8))
        }
    }

    var syncClipboard: Boolean
        get() = prefs.getBoolean("sync_clipboard", true)
        set(v) = prefs.edit().putBoolean("sync_clipboard", v).apply()

    var acceptFiles: Boolean
        get() = prefs.getBoolean("accept_files", true)
        set(v) = prefs.edit().putBoolean("accept_files", v).apply()

    // ---- Optional features: each one has its own switch; off means nothing runs for it. -----------------

    /** Show this phone's notifications on the PC (needs notification access). */
    var featNotify: Boolean
        get() = prefs.getBoolean("feat_notify", false)
        set(v) = prefs.edit().putBoolean("feat_notify", v).apply()

    /** Allow replying and tapping notification buttons from the PC. */
    var notifyReply: Boolean
        get() = prefs.getBoolean("notify_reply", true)
        set(v) = prefs.edit().putBoolean("notify_reply", v).apply()

    /** Share battery level both ways and alert about the PC's battery. */
    var featBattery: Boolean
        get() = prefs.getBoolean("feat_battery", false)
        set(v) = prefs.edit().putBoolean("feat_battery", v).apply()

    var batteryAlerts: Boolean
        get() = prefs.getBoolean("battery_alerts", true)
        set(v) = prefs.edit().putBoolean("battery_alerts", v).apply()

    /** Charge level for the "unplug" alert: 80, 90 or 100. */
    var batteryFullAt: Int
        get() = prefs.getInt("battery_full_at", 80)
        set(v) = prefs.edit().putInt("battery_full_at", v).apply()

    /** Find my phone / find my PC. */
    var featRing: Boolean
        get() = prefs.getBoolean("feat_ring", false)
        set(v) = prefs.edit().putBoolean("feat_ring", v).apply()

    /** Music control (master switch). */
    var featMedia: Boolean
        get() = prefs.getBoolean("feat_media", true)
        set(v) = prefs.edit().putBoolean("feat_media", v).apply()

    /** Show this phone's music on the PC and let the PC control it (needs notification access). */
    var mediaPhoneToPc: Boolean
        get() = prefs.getBoolean("media_phone_to_pc", true)
        set(v) = prefs.edit().putBoolean("media_phone_to_pc", v).apply()

    /** Show the PC's music in this phone's media controls. */
    var mediaPcToPhone: Boolean
        get() = prefs.getBoolean("media_pc_to_phone", true)
        set(v) = prefs.edit().putBoolean("media_pc_to_phone", v).apply()

    /** Audio between the devices (master switch): this phone's sound on the PC, the PC's sound here. */
    var featAudio: Boolean
        get() = prefs.getBoolean("feat_audio", false)
        set(v) = prefs.edit().putBoolean("feat_audio", v).apply()

    /** Offer "Play on PC": the PC becomes this phone's Bluetooth speaker. */
    var audioPhoneToPc: Boolean
        get() = prefs.getBoolean("audio_phone_to_pc", true)
        set(v) = prefs.edit().putBoolean("audio_phone_to_pc", v).apply()

    /** Play the PC's sound on this phone (over Wi-Fi). */
    var audioPcToPhone: Boolean
        get() = prefs.getBoolean("audio_pc_to_phone", true)
        set(v) = prefs.edit().putBoolean("audio_pc_to_phone", v).apply()

    private companion object {
        const val IDENTITY = "identity.p12.enc"
        const val DEVICES = "devices.json.enc"

        // The PKCS#12 blob is already encrypted by SecureFiles; this password only satisfies the format.
        val PKCS12_PASSWORD = "midfielder".toCharArray()
    }
}
