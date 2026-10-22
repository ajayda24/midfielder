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
 *
 * Reading tells "not there" apart from "couldn't decrypt": a Keystore hiccup must never look like a fresh install,
 * or the phone would make a new identity and silently lose its pairing. Writes are atomic (fsync, then rename).
 */
class SecureFiles(context: Context) {
    private val dir = File(context.noBackupFilesDir, "secure").apply { mkdirs() }

    sealed interface Read {
        class Ok(val bytes: ByteArray) : Read
        data object Missing : Read
        class Failed(val error: Throwable) : Read
    }

    /** Reads and decrypts [name], retrying a couple of times: the Keystore can fail transiently on some devices. */
    fun readResult(name: String): Read {
        val f = File(dir, name)
        if (!f.exists()) return Read.Missing
        var last: Throwable? = null
        repeat(3) { attempt ->
            try {
                val blob = android.util.AtomicFile(f).readFully()
                val iv = blob.copyOfRange(0, IV_BYTES)
                val cipher = Cipher.getInstance(TRANSFORMATION)
                cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
                return Read.Ok(cipher.doFinal(blob, IV_BYTES, blob.size - IV_BYTES))
            } catch (e: Exception) {
                last = e
                if (attempt < 2) Thread.sleep(150L * (attempt + 1))
            }
        }
        return Read.Failed(last!!)
    }

    /** The decrypted contents, or null if missing or unreadable (for data that can be rebuilt). */
    fun read(name: String): ByteArray? = (readResult(name) as? Read.Ok)?.bytes

    /** Keeps an unreadable file aside (never overwritten) so a later fix or a support check can still use it. */
    fun quarantine(name: String) {
        val f = File(dir, name)
        if (f.exists()) f.renameTo(File(dir, "$name.bad-${System.currentTimeMillis()}"))
    }

    fun delete(name: String) {
        android.util.AtomicFile(File(dir, name)).delete()
    }

    fun write(name: String, data: ByteArray) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val out = cipher.iv + cipher.doFinal(data)
        val file = android.util.AtomicFile(File(dir, name))
        val stream = file.startWrite()
        try {
            stream.write(out)
            file.finishWrite(stream) // fsyncs before replacing the old file
        } catch (e: Exception) {
            file.failWrite(stream)
            throw e
        }
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

    /** Set when stored data couldn't be read and had to be set aside (the UI tells the user to pair again). */
    @Volatile
    var storageProblem: String? = null
        private set

    /**
     * This phone's identity. A new one is made only on a genuinely fresh install; an unreadable one is kept aside
     * (not overwritten) and reported, since a new identity means every PC has to pair again.
     */
    fun loadOrCreateIdentity(): DeviceIdentity {
        when (val r = files.readResult(IDENTITY)) {
            is SecureFiles.Read.Ok -> {
                runCatching { return DeviceIdentity.fromPkcs12(r.bytes, PKCS12_PASSWORD) }
                    .onFailure { EventLog.error("The stored identity is damaged", it) }
                files.quarantine(IDENTITY)
                storageProblem = "Midfielder's saved identity was damaged. Pair with your PC again."
            }
            is SecureFiles.Read.Failed -> {
                EventLog.error("Couldn't unlock the stored identity", r.error)
                files.quarantine(IDENTITY)
                storageProblem = "Android couldn't unlock Midfielder's saved keys. Pair with your PC again."
            }
            SecureFiles.Read.Missing -> Unit
        }
        val identity = DeviceIdentity.generate()
        files.write(IDENTITY, identity.toPkcs12(PKCS12_PASSWORD))
        return identity
    }

    /** Paired PCs, written through to encrypted storage on every change (rare). Loaded on first use. */
    val trustStore: TrustStore by lazy { createTrustStore() }

    private fun createTrustStore(): TrustStore = object : TrustStore {
        private val map = ConcurrentHashMap<Fingerprint, PairedDevice>().apply {
            val bytes = when (val r = files.readResult(DEVICES)) {
                is SecureFiles.Read.Ok -> r.bytes
                is SecureFiles.Read.Failed -> {
                    // Keep it aside rather than overwrite it with an empty list on the next pairing.
                    EventLog.error("Couldn't unlock the paired-PC list", r.error)
                    files.quarantine(DEVICES)
                    storageProblem = storageProblem ?: "Android couldn't unlock the list of paired PCs. Pair with your PC again."
                    null
                }
                SecureFiles.Read.Missing -> null
            }
            bytes?.let {
                runCatching {
                    val arr = JSONArray(String(it, Charsets.UTF_8))
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

    /** Calls on the PC: caller ID (master switch). */
    var featCalls: Boolean
        get() = prefs.getBoolean("feat_calls", false)
        set(v) = prefs.edit().putBoolean("feat_calls", v).apply()

    /** Answer, decline and end calls from the PC. */
    var callsActions: Boolean
        get() = prefs.getBoolean("calls_actions", true)
        set(v) = prefs.edit().putBoolean("calls_actions", v).apply()

    /** Show contact names and photos (needs contacts access). */
    var callsContacts: Boolean
        get() = prefs.getBoolean("calls_contacts", true)
        set(v) = prefs.edit().putBoolean("calls_contacts", v).apply()

    /** Decline with a quick text from the PC (needs SMS access). */
    var callsSms: Boolean
        get() = prefs.getBoolean("calls_sms", false)
        set(v) = prefs.edit().putBoolean("calls_sms", v).apply()

    var callsSmsText: String
        get() = prefs.getString("calls_sms_text", null) ?: "Can't talk now, I'll call you back."
        set(v) = prefs.edit().putString("calls_sms_text", v).apply()

    /** Lock the PC when this phone walks away (Bluetooth beacon). */
    var featPresence: Boolean
        get() = prefs.getBoolean("feat_presence", false)
        set(v) = prefs.edit().putBoolean("feat_presence", v).apply()

    /**
     * Auto-lock beacon keys, one per PC (fingerprint hex → key), encrypted at rest and cached in memory: they're
     * read on every feature refresh, which must not touch the Keystore each time.
     */
    val presenceKeys: Map<String, ByteArray>
        get() = loadPresenceKeys()

    fun setPresenceKey(pcFingerprint: String, key: ByteArray?) {
        synchronized(PresenceCache) {
            val next = loadPresenceKeys().toMutableMap()
            if (key == null) next.remove(pcFingerprint) else next[pcFingerprint] = key
            val json = JSONObject()
            for ((fp, k) in next) json.put(fp, android.util.Base64.encodeToString(k, android.util.Base64.NO_WRAP))
            if (next.isEmpty()) files.delete(PRESENCE_KEYS) else files.write(PRESENCE_KEYS, json.toString().toByteArray())
            PresenceCache.keys = next
        }
    }

    fun clearPresenceKeys() = synchronized(PresenceCache) {
        files.delete(PRESENCE_KEYS)
        files.delete(LEGACY_PRESENCE_KEY)
        PresenceCache.keys = emptyMap()
    }

    private fun loadPresenceKeys(): Map<String, ByteArray> {
        PresenceCache.keys?.let { return it }
        synchronized(PresenceCache) {
            PresenceCache.keys?.let { return it }
            files.delete(LEGACY_PRESENCE_KEY) // one key for every PC: replaced by per-PC keys (each PC resends its key)
            val loaded = files.read(PRESENCE_KEYS)?.let { bytes ->
                runCatching {
                    val json = JSONObject(String(bytes))
                    json.keys().asSequence().associateWith { android.util.Base64.decode(json.getString(it), android.util.Base64.NO_WRAP) }
                }.getOrNull()
            }.orEmpty()
            PresenceCache.keys = loaded
            return loaded
        }
    }

    /** Process-wide: every AppStorage instance shares the same decrypted keys. */
    private object PresenceCache {
        @Volatile
        var keys: Map<String, ByteArray>? = null
    }

    private companion object {
        const val LEGACY_PRESENCE_KEY = "presence.key.enc"
        const val PRESENCE_KEYS = "presence.keys.enc"
        const val IDENTITY = "identity.p12.enc"
        const val DEVICES = "devices.json.enc"

        // The PKCS#12 blob is already encrypted by SecureFiles; this password only satisfies the format.
        val PKCS12_PASSWORD = "midfielder".toCharArray()
    }
}
