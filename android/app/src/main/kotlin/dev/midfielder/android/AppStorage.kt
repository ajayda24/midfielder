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

    private companion object {
        const val IDENTITY = "identity.p12.enc"
        const val DEVICES = "devices.json.enc"

        // The PKCS#12 blob is already encrypted by SecureFiles; this password only satisfies the format.
        val PKCS12_PASSWORD = "midfielder".toCharArray()
    }
}
