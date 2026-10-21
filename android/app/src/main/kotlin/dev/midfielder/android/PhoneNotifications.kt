package dev.midfielder.android

import android.app.Notification
import android.app.NotificationManager
import android.app.RemoteInput
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import dev.midfielder.proto.NotificationAction
import dev.midfielder.proto.NotificationButton
import dev.midfielder.proto.NotificationPosted
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream

/**
 * Android binds this once the user grants notification access. It only hands events to [PhoneNotifications];
 * the component is disabled whenever both features that need it (notifications on PC, phone music on PC) are off,
 * so Android doesn't bind it at all then.
 */
class PhoneNotificationListener : NotificationListenerService() {
    override fun onListenerConnected() {
        current = this
        SyncEngine.current?.features?.onListenerChanged()
    }

    override fun onListenerDisconnected() {
        if (current === this) current = null
        SyncEngine.current?.features?.onListenerChanged()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        SyncEngine.current?.features?.notifications?.posted(this, sbn)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        SyncEngine.current?.features?.notifications?.removed(sbn.key)
    }

    companion object {
        @Volatile
        var current: PhoneNotificationListener? = null

        fun component(context: Context) = ComponentName(context, PhoneNotificationListener::class.java)

        fun isGranted(context: Context): Boolean =
            context.getSystemService(NotificationManager::class.java).isNotificationListenerAccessGranted(component(context))

        /** Enables or disables the service component, so Android only binds it while a feature needs it. */
        fun setEnabled(context: Context, enabled: Boolean) {
            val state = if (enabled) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
            val pm = context.packageManager
            if (pm.getComponentEnabledSetting(component(context)) == state) return
            pm.setComponentEnabledSetting(component(context), state, PackageManager.DONT_KILL_APP)
            if (enabled) NotificationListenerService.requestRebind(component(context))
        }
    }
}

/**
 * Turns phone notifications into [NotificationPosted] messages for the PC and carries out the PC's replies,
 * button taps and dismissals. Skips anything silent, ongoing, media, progress or marked secret.
 */
class PhoneNotifications(
    private val app: Context,
    private val storage: AppStorage,
    private val send: (NotificationPosted?, String?) -> Unit,
) {
    private class Sent(val sbn: StatusBarNotification, val title: String, val text: String)

    // Most recent first; bounded so a chatty phone can't grow it.
    private val sent = object : LinkedHashMap<String, Sent>(64, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Sent>) = size > MAX_TRACKED
    }
    private val icons = HashMap<String, ByteArray>()

    fun posted(listener: NotificationListenerService, sbn: StatusBarNotification) {
        if (!storage.featNotify || !worthForwarding(listener, sbn)) return
        val n = sbn.notification
        val extras = n.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty().take(MAX_TITLE)
        val text = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: extras.getCharSequence(Notification.EXTRA_TEXT))
            ?.toString().orEmpty().take(MAX_TEXT)
        if (title.isBlank() && text.isBlank()) return
        synchronized(sent) {
            val previous = sent[sbn.key]
            if (previous != null && previous.title == title && previous.text == text) return
            sent[sbn.key] = Sent(sbn, title, text)
        }
        val actions = n.actions.orEmpty()
        val canReply = storage.notifyReply && actions.any { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }
        val buttons = if (!storage.notifyReply) emptyList() else actions.withIndex()
            .filter { (_, a) -> a.remoteInputs.isNullOrEmpty() && !a.title.isNullOrBlank() }
            .take(MAX_BUTTONS)
            .map { (i, a) -> NotificationButton(id = i.toString(), title = a.title.toString().take(MAX_BUTTON_TITLE)) }
        send(
            NotificationPosted(
                key = sbn.key,
                app_package = sbn.packageName,
                app_name = appName(sbn),
                title = title,
                text = text,
                posted_at_ms = sbn.postTime,
                icon_png = icon(sbn)?.toByteString() ?: okio.ByteString.EMPTY,
                can_reply = canReply,
                buttons = buttons,
            ),
            null,
        )
    }

    fun removed(key: String) {
        val known = synchronized(sent) { sent.remove(key) != null }
        if (known && storage.featNotify) send(null, key)
    }

    /** The PC replied, tapped a button or dismissed a notification. */
    fun act(action: NotificationAction) {
        val entry = synchronized(sent) { sent[action.key] } ?: return
        val listener = PhoneNotificationListener.current
        val actions = entry.sbn.notification.actions.orEmpty()
        runCatching {
            when {
                action.dismiss -> listener?.cancelNotification(action.key)
                action.reply_text.isNotEmpty() && storage.notifyReply -> {
                    val reply = actions.firstOrNull { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true } ?: return
                    val inputs = reply.remoteInputs
                    val results = Bundle().apply {
                        for (input in inputs) putCharSequence(input.resultKey, action.reply_text.take(MAX_REPLY))
                    }
                    val fill = Intent()
                    RemoteInput.addResultsToIntent(inputs, fill, results)
                    reply.actionIntent.send(app, 0, fill)
                    EventLog.add("Replied to ${entry.title.ifEmpty { "a notification" }} from the PC")
                }
                action.button_id.isNotEmpty() && storage.notifyReply ->
                    actions.getOrNull(action.button_id.toIntOrNull() ?: -1)?.actionIntent?.send()
            }
        }.onFailure { EventLog.error("Couldn't act on a notification from the PC", it) }
    }

    fun clear() = synchronized(sent) { sent.clear() }

    private fun worthForwarding(listener: NotificationListenerService, sbn: StatusBarNotification): Boolean {
        val n = sbn.notification
        if (sbn.packageName == app.packageName || sbn.isOngoing) return false
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return false
        if (n.visibility == Notification.VISIBILITY_SECRET) return false
        if (n.category in SKIPPED_CATEGORIES) return false
        if (n.extras.getString(Notification.EXTRA_TEMPLATE)?.contains("MediaStyle") == true) return false
        if (n.extras.containsKey(Notification.EXTRA_PROGRESS_MAX) && n.extras.getInt(Notification.EXTRA_PROGRESS_MAX) > 0) return false
        // Silent notifications (low importance channels) stay on the phone.
        val ranking = NotificationListenerService.Ranking()
        val rankings = listener.currentRanking ?: return true
        return !rankings.getRanking(sbn.key, ranking) || ranking.importance >= NotificationManager.IMPORTANCE_DEFAULT
    }

    /**
     * The posting app's info travels inside the notification, so no package-visibility permission is needed to
     * name it or draw its icon.
     */
    @Suppress("DEPRECATION")
    private fun appInfo(sbn: StatusBarNotification): ApplicationInfo? =
        sbn.notification.extras.getParcelable<ApplicationInfo>(EXTRA_APP_INFO)
            ?: runCatching { app.packageManager.getApplicationInfo(sbn.packageName, 0) }.getOrNull()

    private fun appName(sbn: StatusBarNotification): String =
        (appInfo(sbn)?.let { runCatching { app.packageManager.getApplicationLabel(it).toString() }.getOrNull() } ?: sbn.packageName)
            .take(MAX_TITLE)

    private fun icon(sbn: StatusBarNotification): ByteArray? = synchronized(icons) {
        icons.getOrPut(sbn.packageName) {
            runCatching {
                val info = appInfo(sbn) ?: error("no app info")
                val drawable = app.packageManager.getApplicationIcon(info)
                val bmp = Bitmap.createBitmap(ICON_PX, ICON_PX, Bitmap.Config.ARGB_8888)
                drawable.setBounds(0, 0, ICON_PX, ICON_PX)
                drawable.draw(Canvas(bmp))
                ByteArrayOutputStream().use { out ->
                    bmp.compress(Bitmap.CompressFormat.PNG, 100, out)
                    bmp.recycle()
                    out.toByteArray()
                }
            }.getOrNull()?.takeIf { it.size <= MAX_ICON_BYTES } ?: ByteArray(0)
        }.takeIf { it.isNotEmpty() }
    }

    private companion object {
        const val EXTRA_APP_INFO = "android.appInfo" // Notification.EXTRA_BUILDER_APPLICATION_INFO (hidden constant)
        const val MAX_TRACKED = 100
        const val MAX_TITLE = 200
        const val MAX_TEXT = 1_000
        const val MAX_BUTTONS = 3
        const val MAX_BUTTON_TITLE = 40
        const val MAX_REPLY = 2_000
        const val ICON_PX = 48
        const val MAX_ICON_BYTES = 8 * 1024
        val SKIPPED_CATEGORIES = setOf(
            Notification.CATEGORY_TRANSPORT, Notification.CATEGORY_PROGRESS, Notification.CATEGORY_SERVICE,
            Notification.CATEGORY_SYSTEM, Notification.CATEGORY_NAVIGATION, Notification.CATEGORY_STOPWATCH,
            Notification.CATEGORY_CALL, // calls get their own feature
        )
    }
}
