package dev.midfielder.android

import android.Manifest
import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.AudioManager
import android.net.Uri
import android.provider.ContactsContract
import android.telecom.TelecomManager
import android.telephony.SmsManager
import android.telephony.TelephonyManager
import dev.midfielder.proto.CallAction
import dev.midfielder.proto.CallCommand
import dev.midfielder.proto.CallPhase
import dev.midfielder.proto.CallState
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors

/**
 * Calls on the PC: follows the phone's call state (a receiver registered only while the feature is on) and sends a
 * [CallState] with caller number, contact name and photo; carries out the PC's [CallCommand]s (answer, decline,
 * end, silence, decline with a quick SMS). Each part needs its own permission and its own switch.
 */
class CallBridge(context: Context, private val storage: AppStorage, private val send: (CallState) -> Unit) {
    private val app = context.applicationContext
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "calls").apply { isDaemon = true } }
    private var receiver: BroadcastReceiver? = null

    // Touched only on [worker].
    private var callId = 0
    private var phase = CallPhase.CALL_PHASE_ENDED
    private var number = ""
    private var startedAt = 0L
    private var ringMuted = false

    @Volatile
    var current: CallState? = null
        private set

    val isActive: Boolean get() = current?.phase == CallPhase.CALL_PHASE_ACTIVE

    fun start() {
        if (receiver != null || !granted(app, Manifest.permission.READ_PHONE_STATE)) return
        val r = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                val state = intent.getStringExtra(TelephonyManager.EXTRA_STATE) ?: return
                @Suppress("DEPRECATION") // still the only way to get the ringing number without being the dialer
                val incoming = intent.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER).orEmpty()
                worker.execute { onState(state, incoming) }
            }
        }
        receiver = r
        // A protected system broadcast: only Android can send it.
        app.registerReceiver(r, IntentFilter(TelephonyManager.ACTION_PHONE_STATE_CHANGED))
    }

    fun stop() {
        receiver?.let { runCatching { app.unregisterReceiver(it) } }
        receiver = null
        worker.execute {
            restoreRinger()
            phase = CallPhase.CALL_PHASE_ENDED
            current = null
        }
    }

    /** Re-sends the current call (a PC just connected, or a switch changed). */
    fun resend() = worker.execute { current?.let { send(build()) } }

    fun command(cmd: CallCommand) = worker.execute { run(cmd) }

    private fun onState(state: String, incoming: String) {
        val now = System.currentTimeMillis()
        when (state) {
            TelephonyManager.EXTRA_STATE_RINGING -> {
                if (phase != CallPhase.CALL_PHASE_RINGING) {
                    callId++
                    phase = CallPhase.CALL_PHASE_RINGING
                    startedAt = now
                    number = incoming
                } else if (incoming.isNotEmpty() && incoming != number) {
                    number = incoming // the second broadcast (with call-log permission) carries the number
                } else {
                    return
                }
            }
            TelephonyManager.EXTRA_STATE_OFFHOOK -> {
                if (phase == CallPhase.CALL_PHASE_ACTIVE) return
                if (phase == CallPhase.CALL_PHASE_ENDED) {
                    callId++ // an outgoing call
                    number = ""
                }
                phase = CallPhase.CALL_PHASE_ACTIVE
                startedAt = now
            }
            TelephonyManager.EXTRA_STATE_IDLE -> {
                if (phase == CallPhase.CALL_PHASE_ENDED) return
                phase = CallPhase.CALL_PHASE_ENDED
                startedAt = now
                restoreRinger()
            }
            else -> return
        }
        val s = build()
        current = s.takeIf { phase != CallPhase.CALL_PHASE_ENDED }
        send(s)
        EventLog.add("Call ${phase.name.removePrefix("CALL_PHASE_").lowercase()}")
    }

    private fun build(): CallState {
        val contact = if (storage.callsContacts && number.isNotEmpty()) lookup(number) else null
        return CallState(
            call_id = callId,
            phase = phase,
            number = number,
            contact_name = contact?.first.orEmpty(),
            photo = contact?.second?.toByteString() ?: okio.ByteString.EMPTY,
            started_at_ms = startedAt,
            can_answer = storage.callsActions && granted(app, Manifest.permission.ANSWER_PHONE_CALLS),
            can_sms = storage.callsSms && granted(app, Manifest.permission.SEND_SMS),
            sms_text = storage.callsSmsText,
        )
    }

    @SuppressLint("MissingPermission") // each action checks its own permission first
    private fun run(cmd: CallCommand) {
        if (cmd.call_id != callId || phase == CallPhase.CALL_PHASE_ENDED) return
        val telecom = app.getSystemService(TelecomManager::class.java) ?: return
        val canAct = storage.callsActions && granted(app, Manifest.permission.ANSWER_PHONE_CALLS)
        runCatching {
            when (cmd.action) {
                CallAction.CALL_ACTION_ANSWER -> if (canAct) {
                    @Suppress("DEPRECATION") // no replacement for apps that aren't the default dialer
                    telecom.acceptRingingCall()
                }
                CallAction.CALL_ACTION_DECLINE, CallAction.CALL_ACTION_END -> if (canAct) endCall(telecom)
                CallAction.CALL_ACTION_SILENCE -> silence(telecom)
                CallAction.CALL_ACTION_SMS_REPLY -> if (storage.callsSms && granted(app, Manifest.permission.SEND_SMS) && number.isNotEmpty()) {
                    sms().sendTextMessage(number, null, cmd.text.ifEmpty { storage.callsSmsText }, null, null)
                    EventLog.add("Sent the quick reply to the caller")
                    if (canAct) endCall(telecom)
                }
                else -> Unit
            }
        }.onFailure { EventLog.error("Couldn't ${cmd.action.name.removePrefix("CALL_ACTION_").lowercase()} the call", it) }
    }

    @SuppressLint("MissingPermission")
    private fun endCall(telecom: TelecomManager) {
        @Suppress("DEPRECATION")
        telecom.endCall()
    }

    @SuppressLint("MissingPermission")
    private fun silence(telecom: TelecomManager) {
        try {
            telecom.silenceRinger()
        } catch (_: SecurityException) {
            // Not the default dialer: mute the ring stream until the call ends.
            runCatching {
                app.getSystemService(AudioManager::class.java)?.adjustStreamVolume(AudioManager.STREAM_RING, AudioManager.ADJUST_MUTE, 0)
                ringMuted = true
            }
        }
    }

    private fun restoreRinger() {
        if (!ringMuted) return
        ringMuted = false
        runCatching { app.getSystemService(AudioManager::class.java)?.adjustStreamVolume(AudioManager.STREAM_RING, AudioManager.ADJUST_UNMUTE, 0) }
    }

    private fun sms(): SmsManager =
        if (android.os.Build.VERSION.SDK_INT >= 31) app.getSystemService(SmsManager::class.java)
        else @Suppress("DEPRECATION") SmsManager.getDefault()

    /** Contact name and a small JPEG photo for [phone], if contacts are allowed and it's a contact. */
    private fun lookup(phone: String): Pair<String, ByteArray?>? {
        if (!granted(app, Manifest.permission.READ_CONTACTS)) return null
        val uri = Uri.withAppendedPath(ContactsContract.PhoneLookup.CONTENT_FILTER_URI, Uri.encode(phone))
        val cols = arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME, ContactsContract.PhoneLookup.PHOTO_THUMBNAIL_URI)
        return runCatching {
            app.contentResolver.query(uri, cols, null, null, null)?.use { c ->
                if (!c.moveToFirst()) return@use null
                val name = c.getString(0).orEmpty()
                val photo = c.getString(1)?.let { photoJpeg(Uri.parse(it)) }
                name to photo
            }
        }.getOrNull()
    }

    private fun photoJpeg(uri: Uri): ByteArray? = runCatching {
        val src = app.contentResolver.openInputStream(uri)?.use(BitmapFactory::decodeStream) ?: return null
        val bmp = if (src.width > PHOTO_PX) Bitmap.createScaledBitmap(src, PHOTO_PX, PHOTO_PX * src.height / src.width, true) else src
        ByteArrayOutputStream().use { out ->
            bmp.compress(Bitmap.CompressFormat.JPEG, 80, out)
            out.toByteArray()
        }.takeIf { it.size <= MAX_PHOTO_BYTES }
    }.getOrNull()

    companion object {
        private const val PHOTO_PX = 96
        private const val MAX_PHOTO_BYTES = 16 * 1024

        fun granted(context: Context, permission: String): Boolean =
            context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

        /** Permissions the switched-on call options still need. */
        fun missing(context: Context, storage: AppStorage): List<String> = buildList {
            if (!storage.featCalls) return@buildList
            add(Manifest.permission.READ_PHONE_STATE)
            add(Manifest.permission.READ_CALL_LOG)
            if (storage.callsActions) add(Manifest.permission.ANSWER_PHONE_CALLS)
            if (storage.callsContacts) add(Manifest.permission.READ_CONTACTS)
            if (storage.callsSms) add(Manifest.permission.SEND_SMS)
        }.filterNot { granted(context, it) }
    }
}
