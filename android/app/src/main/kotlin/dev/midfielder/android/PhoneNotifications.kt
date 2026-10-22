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
 * button taps and dismissals. Skips anything silent, ongoing, media, progress, local-only, from a work profile or
 * hidden on the lock screen. A notification counts as sent only once a PC actually got it, so a PC that connects
 * later still gets what's on the phone.
 */
class PhoneNotifications(
    private val app: Context,
    private val storage: AppStorage,
    /** Sends a post (or a removal key); true if at least one PC got it. */
    private val send: (NotificationPosted?, String?) -> Boolean,
) {
    /** What's needed to act on a notification later; the latest actions (apps rebuild their PendingIntents). */
    private class Sent(val title: String, val text: String, @Volatile var actions: Array<Notification.Action>, @Volatile var delivered: Boolean)

    // Most recent first; bounded so a chatty phone can't grow it (evicted ones are removed on the PC too).
    private val sent = LinkedHashMap<String, Sent>(64, 0.75f, true)
    private val icons = HashMap<String, ByteArray>()
    private val labels = HashMap<String, String>()

    fun posted(listener: NotificationListenerService, sbn: StatusBarNotification) {
        // Another app's extras can hold classes we can't unparcel (Android 10–12): never let that crash us.
        runCatching { postedUnsafe(listener, sbn) }.onFailure { EventLog.error("Skipped a notification", it) }
    }

    private fun postedUnsafe(listener: NotificationListenerService, sbn: StatusBarNotification) {
        if (!storage.featNotify || !worthForwarding(listener, sbn)) return
        val n = sbn.notification
        val (title, text) = titleAndText(n)
        if (title.isBlank() && text.isBlank()) return
        val actions: Array<Notification.Action> = n.actions?.filterNotNull()?.toTypedArray() ?: emptyArray()
        val evicted = ArrayList<String>()
        synchronized(sent) {
            val previous = sent[sbn.key]
            if (previous != null && previous.title == title && previous.text == text && previous.delivered) {
                previous.actions = actions // same content, fresh intents
                return
            }
            sent[sbn.key] = Sent(title, text, actions, delivered = false)
            while (sent.size > MAX_TRACKED) {
                val eldest = sent.keys.first()
                sent.remove(eldest)
                evicted += eldest
            }
        }
        for (key in evicted) send(null, key)
        val canReply = storage.notifyReply && replyAction(actions) != null
        val buttons = if (!storage.notifyReply) emptyList() else actions.withIndex()
            .filter { (_, a) -> a.remoteInputs.isNullOrEmpty() && !a.title.isNullOrBlank() && canFireFromBackground(a) }
            .take(MAX_BUTTONS)
            .map { (i, a) -> NotificationButton(id = i.toString(), title = a.title.toString().take(MAX_BUTTON_TITLE)) }
        val delivered = send(
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
        synchronized(sent) { sent[sbn.key]?.delivered = delivered }
    }

    /** A PC connected (or turned the feature on): send what's on the phone now. */
    fun resendActive() {
        val listener = PhoneNotificationListener.current ?: return
        synchronized(sent) { for (e in sent.values) e.delivered = false }
        val active = runCatching { listener.activeNotifications }.getOrNull().orEmpty()
        for (sbn in active) posted(listener, sbn)
    }

    fun removed(key: String) {
        val known = synchronized(sent) { sent.remove(key) != null }
        if (known && storage.featNotify) send(null, key)
    }

    /** The PC replied, tapped a button or dismissed a notification. */
    fun act(action: NotificationAction) {
        val entry = synchronized(sent) { sent[action.key] } ?: return
        val listener = PhoneNotificationListener.current
        val actions = entry.actions
        runCatching {
            when {
                action.dismiss -> listener?.cancelNotification(action.key)
                action.reply_text.isNotEmpty() && storage.notifyReply -> {
                    val reply = replyAction(actions) ?: return
                    // Only the free-form box gets the text; choice-only inputs (quick replies) stay empty.
                    val inputs = reply.remoteInputs.filter { it.allowFreeFormInput }.toTypedArray()
                    val results = Bundle().apply {
                        for (input in inputs) putCharSequence(input.resultKey, action.reply_text.take(MAX_REPLY))
                    }
                    val fill = Intent()
                    RemoteInput.addResultsToIntent(inputs, fill, results)
                    RemoteInput.setResultsSource(fill, RemoteInput.SOURCE_FREE_FORM_INPUT)
                    reply.actionIntent.send(app, 0, fill)
                    EventLog.add("Replied to ${entry.title.ifEmpty { "a notification" }} from the PC")
                }
                action.button_id.isNotEmpty() && storage.notifyReply ->
                    actions.getOrNull(action.button_id.toIntOrNull() ?: -1)?.actionIntent?.send()
            }
        }.onFailure { EventLog.error("Couldn't act on a notification from the PC", it) }
    }

    fun clear() = synchronized(sent) { sent.clear() }

    /** Prefer the action that says it's a reply; else any action with a free-form text box. */
    private fun replyAction(actions: Array<Notification.Action>): Notification.Action? {
        val withBox = actions.filter { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }
        return withBox.firstOrNull { it.semanticAction == Notification.Action.SEMANTIC_ACTION_REPLY } ?: withBox.firstOrNull()
    }

    /** Android 12+ blocks starting an activity from the background: such a button would do nothing from the PC. */
    private fun canFireFromBackground(a: Notification.Action): Boolean =
        android.os.Build.VERSION.SDK_INT < 31 || a.actionIntent?.isActivity != true

    /** Chats show their latest message (and the conversation name for groups), not the summary line. */
    private fun titleAndText(n: Notification): Pair<String, String> {
        val extras = n.extras
        var title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        var text = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
        @Suppress("DEPRECATION")
        val messages = extras.getParcelableArray(Notification.EXTRA_MESSAGES)
        val last = (messages?.lastOrNull() as? Bundle)
        if (last != null) {
            val body = last.getCharSequence("text")?.toString()
            val sender = last.getCharSequence("sender")?.toString()
            val conversation = extras.getCharSequence(Notification.EXTRA_CONVERSATION_TITLE)?.toString()
            if (!body.isNullOrBlank()) text = if (conversation != null && sender != null) "$sender: $body" else body
            if (!conversation.isNullOrBlank()) title = conversation
            else if (!sender.isNullOrBlank()) title = sender
        }
        return title.take(MAX_TITLE) to text.take(MAX_TEXT)
    }

    private fun worthForwarding(listener: NotificationListenerService, sbn: StatusBarNotification): Boolean {
        val n = sbn.notification
        if (sbn.packageName == app.packageName || sbn.isOngoing) return false
        // The app asked for it to stay on this device (e.g. a wearable bridge would skip it too).
        if (n.flags and (Notification.FLAG_GROUP_SUMMARY or Notification.FLAG_LOCAL_ONLY or Notification.FLAG_FOREGROUND_SERVICE) != 0) return false
        if (sbn.user != android.os.Process.myUserHandle()) return false // work profile stays on the phone
        if (n.visibility == Notification.VISIBILITY_SECRET) return false
        if (n.category in SKIPPED_CATEGORIES) return false
        if (n.extras.getString(Notification.EXTRA_TEMPLATE)?.contains("MediaStyle") == true) return false
        if (n.extras.containsKey(Notification.EXTRA_PROGRESS_MAX) && n.extras.getInt(Notification.EXTRA_PROGRESS_MAX) > 0) return false
        // Silent notifications (low importance channels) and channels hidden from the lock screen stay on the phone.
        val ranking = NotificationListenerService.Ranking()
        val rankings = listener.currentRanking ?: return true
        if (!rankings.getRanking(sbn.key, ranking)) return true
        if (ranking.channel?.lockscreenVisibility == Notification.VISIBILITY_SECRET) return false
        return ranking.importance >= NotificationManager.IMPORTANCE_DEFAULT
    }

    /**
     * The posting app's info travels inside the notification, so no package-visibility permission is needed to
     * name it or draw its icon.
     */
    @Suppress("DEPRECATION")
    private fun appInfo(sbn: StatusBarNotification): ApplicationInfo? =
        sbn.notification.extras.getParcelable<ApplicationInfo>(EXTRA_APP_INFO)
            ?: runCatching { app.packageManager.getApplicationInfo(sbn.packageName, 0) }.getOrNull()

    private fun appName(sbn: StatusBarNotification): String = synchronized(labels) {
        labels.getOrPut(sbn.packageName) {
            (appInfo(sbn)?.let { runCatching { app.packageManager.getApplicationLabel(it).toString() }.getOrNull() } ?: sbn.packageName)
                .take(MAX_TITLE)
        }
    }

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
