package dev.midfielder.android.ui

import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import dev.midfielder.android.AppStorage
import dev.midfielder.android.FileActions
import dev.midfielder.android.HistoryStore
import dev.midfielder.android.MainActivity
import dev.midfielder.android.PhoneNotificationListener
import dev.midfielder.android.R
import dev.midfielder.android.Setup
import dev.midfielder.android.SyncEngine
import dev.midfielder.core.feature.Features
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** Sync switches, the automatic-copy checklist, the paired PC and storage. */
class SettingsTab(host: MainActivity) : Tab(host) {
    private lateinit var checklist: LinearLayout
    private lateinit var pcSection: LinearLayout
    private lateinit var featuresBox: LinearLayout
    private lateinit var storage: AppStorage

    override fun build(): View {
        storage = AppStorage(host)
        val root = page()
        root.addView(ui.headline("Settings").apply { setPadding(ui.dp(4), ui.dp(12), 0, ui.dp(4)) })

        root.addView(ui.sectionHeader("Sync"))
        val sync = ui.card(paddingDp = 8)
        sync.body.addView(ui.switchRow("Sync clipboard", "Copy on one device, paste on the other", storage.syncClipboard) { on ->
            storage.syncClipboard = on
            SyncEngine.current?.clipSync?.enabled = on
        }.indent())
        sync.body.addView(ui.divider())
        sync.body.addView(ui.switchRow("Receive files automatically", "Saved to Download/Midfielder", storage.acceptFiles) {
            storage.acceptFiles = it
        }.indent())
        root.addView(sync.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("Features"))
        root.addView(ui.small("Each feature works when it's on here and in Midfielder on your PC. Off means it doesn't run at all.").apply {
            setPadding(ui.dp(4), 0, ui.dp(4), ui.dp(4))
        })
        featuresBox = ui.vertical()
        root.addView(featuresBox, ui.matchWidth(0))

        root.addView(ui.sectionHeader("Automatic copy setup"))
        val setup = ui.card(paddingDp = 8)
        checklist = setup.body
        root.addView(setup.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("Paired PC"))
        val pc = ui.card()
        pcSection = pc.body
        root.addView(pc.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("Storage"))
        val store = ui.card(paddingDp = 8)
        store.body.addView(
            ui.listRow("Received files", "Download/Midfielder", ui.iconBadge(R.drawable.ic_folder, 40), ui.icon(R.drawable.ic_chevron_right, ui.onSurfaceVariant)) {
                FileActions.openFolder(host)
            }.indent(),
        )
        store.body.addView(ui.divider())
        store.body.addView(
            ui.listRow("Reset statistics", "Clears the counts on Home", ui.iconBadge(R.drawable.ic_delete, 40)) {
                host.confirm("Reset statistics?", "Clip and file counts start again from zero.", "Reset") { HistoryStore.get(host).clearStats() }
            }.indent(),
        )
        root.addView(store.card, ui.matchWidth(4))

        val tip = ui.card(Ui.Card.FILLED, fill = ui.secondaryContainer)
        tip.card.cardElevation = 0f
        tip.body.addView(ui.text("Tip", com.google.android.material.R.style.TextAppearance_Material3_LabelLarge, ui.onSecondaryContainer))
        tip.body.addView(ui.text(
            "Add the \"Send clipboard\" tile to Quick Settings for a one-tap send, and pick your PC in the top row of the share sheet to send files.",
            com.google.android.material.R.style.TextAppearance_Material3_BodyMedium, ui.onSecondaryContainer,
        ).apply { setPadding(0, ui.dp(4), 0, 0) })
        root.addView(tip.card, ui.matchWidth(20))
        return ScrollView(host).apply { addView(root) }
    }

    private fun LinearLayout.indent(): LinearLayout = apply { setPadding(ui.dp(12), paddingTop, ui.dp(12), paddingBottom) }

    override fun start(scope: CoroutineScope) {
        renderChecklist()
        scope.launch { engine().status.collect { renderPc(it) } }
        // Feature cards say whether the PC has each feature on too.
        scope.launch { engine().features.pc.collect { renderFeatures() } }
    }

    /**
     * One card per feature: icon, title, description, a status line and the switch; the feature's options sit inside
     * the same card and slide open while it's on. Off means nothing runs for it.
     */
    private fun renderFeatures() {
        if (!::featuresBox.isInitialized) return
        val box = featuresBox
        android.transition.TransitionManager.beginDelayedTransition(box, android.transition.AutoTransition().setDuration(220))
        box.removeAllViews()
        val pc = SyncEngine.current?.features?.pc?.value
        val granted = PhoneNotificationListener.isGranted(host)
        fun changed(needsAccess: Boolean = false) {
            SyncEngine.current?.features?.apply()
            if (needsAccess && !PhoneNotificationListener.isGranted(host)) openNotificationAccess()
            renderFeatures()
        }

        /** Adds a feature card; returns its options area (empty and hidden while the feature is off). */
        fun card(icon: Int, title: String, description: String, on: Boolean, capability: String, set: (Boolean) -> Unit): LinearLayout {
            val c = ui.card(Ui.Card.FILLED, paddingDp = 0)
            val header = ui.horizontal().apply { setPadding(ui.dp(16), ui.dp(14), ui.dp(12), ui.dp(14)) }
            header.addView(ui.iconBadge(icon, 44, if (on) ui.primaryContainer else ui.secondaryContainer, if (on) ui.onPrimaryContainer else ui.onSecondaryContainer, cornerDp = 14))
            val texts = ui.vertical().apply { setPadding(ui.dp(14), 0, ui.dp(8), 0) }
            texts.addView(ui.title(title))
            texts.addView(ui.supporting(description))
            val (statusText, color) = when {
                !on -> "Off" to ui.onSurfaceVariant
                pc == null || pc.name == null -> "On · works when your PC is connected" to ui.onSurfaceVariant
                capability !in pc.capabilities -> "Turn it on in Midfielder on your PC too" to ui.errorColor
                else -> "On · working with ${pc.name}" to ui.primary
            }
            texts.addView(ui.text(statusText, com.google.android.material.R.style.TextAppearance_Material3_LabelMedium, color).apply {
                setPadding(0, ui.dp(4), 0, 0)
            })
            header.addView(texts, ui.weight())
            header.addView(ui.switch(on) { v ->
                set(v)
                changed(needsAccess = v && capability in setOf(Features.NOTIFY, Features.MEDIA))
            }, ui.wrap())
            c.body.addView(header)
            val options = ui.vertical().apply { setPadding(ui.dp(16), 0, ui.dp(12), ui.dp(8)) }
            if (on) {
                c.body.addView(ui.divider())
                c.body.addView(options)
            }
            box.addView(c.card, ui.matchWidth(10))
            return options
        }
        fun option(parent: LinearLayout, title: String, subtitle: String, on: Boolean, set: (Boolean) -> Unit, access: Boolean = false) =
            parent.addView(ui.switchRow(title, subtitle, on) {
                set(it)
                changed(needsAccess = access && it)
            })
        fun accessPrompt(parent: LinearLayout, why: String) = parent.addView(ui.listRow(
            "Allow notification access", why,
            ui.iconBadge(R.drawable.ic_warning, 40, ui.errorContainer, ui.onErrorContainer),
            below = ui.button("Allow", Ui.Button.TONAL) { openNotificationAccess() },
        ))

        val notify = card(R.drawable.ic_notifications, "Notifications on PC", "See and reply to this phone's notifications on your PC",
            storage.featNotify, Features.NOTIFY) { storage.featNotify = it }
        if (storage.featNotify) {
            if (!granted) accessPrompt(notify, "Needed to read this phone's notifications")
            option(notify, "Reply and actions from PC", "Answer messages and tap notification buttons", storage.notifyReply, { storage.notifyReply = it })
        }

        val battery = card(R.drawable.ic_battery, "Battery sharing", "Your PC's battery here, this phone's on the PC",
            storage.featBattery, Features.BATTERY) { storage.featBattery = it }
        if (storage.featBattery) {
            option(battery, "PC battery alerts", "When the PC is charged to ${storage.batteryFullAt}% or low", storage.batteryAlerts, { storage.batteryAlerts = it })
            if (storage.batteryAlerts) {
                val chips = ui.horizontal().apply { setPadding(ui.dp(4), 0, 0, ui.dp(8)) }
                for (level in listOf(80, 90, 100)) {
                    chips.addView(ui.filterChip("$level%", storage.batteryFullAt == level).apply {
                        setOnClickListener {
                            storage.batteryFullAt = level
                            changed()
                        }
                    }, ui.wrap(if (level == 80) 0 else 8))
                }
                battery.addView(chips)
            }
        }

        card(R.drawable.ic_ring, "Find my phone", "Your PC can ring this phone at full volume, even on silent",
            storage.featRing, Features.RING) { storage.featRing = it }

        val music = card(R.drawable.ic_music, "Music control", "Play, pause and skip across your phone and PC",
            storage.featMedia, Features.MEDIA) { storage.featMedia = it }
        if (storage.featMedia) {
            option(music, "Phone music on PC", "In the Windows media controls and media keys", storage.mediaPhoneToPc, { storage.mediaPhoneToPc = it }, access = true)
            if (storage.mediaPhoneToPc && !granted) accessPrompt(music, "Needed to see what's playing on this phone")
            option(music, "PC music on phone", "In this phone's media controls and lock screen", storage.mediaPcToPhone, { storage.mediaPcToPhone = it })
        }

        val audio = card(R.drawable.ic_headphones, "Audio", "Hear this phone on your PC's speakers, or your PC on this phone",
            storage.featAudio, Features.AUDIO) { storage.featAudio = it }
        if (storage.featAudio) {
            option(audio, "Phone audio on PC", "Your PC becomes this phone's Bluetooth speaker. Pair them over Bluetooth once",
                storage.audioPhoneToPc, { storage.audioPhoneToPc = it })
            option(audio, "PC audio on phone", "Everything your PC plays, over Wi-Fi", storage.audioPcToPhone, { storage.audioPcToPhone = it })
        }
    }

    private fun openNotificationAccess() {
        val intent = if (android.os.Build.VERSION.SDK_INT >= 30) {
            android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                .putExtra(
                    android.provider.Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                    PhoneNotificationListener.component(host).flattenToString(),
                )
        } else {
            android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)
        }
        runCatching { host.startActivity(intent) }
            .onFailure { runCatching { host.startActivity(android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) } }
    }

    fun renderChecklist() {
        renderFeatures()
        if (!::checklist.isInitialized) return
        checklist.removeAllViews()
        Setup.steps(host).forEachIndexed { i, step ->
            if (i > 0) checklist.addView(ui.divider())
            val badge = if (step.done) ui.iconBadge(R.drawable.ic_check, 40, ui.primaryContainer, ui.onPrimaryContainer)
            else ui.iconBadge(R.drawable.ic_warning, 40, ui.errorContainer, ui.onErrorContainer)
            val action = if (step.done) null else ui.button(step.action, Ui.Button.TONAL) {
                step.run(host)
                checklist.postDelayed({ renderChecklist() }, 3_000)
            }
            // The action goes under the text so narrow phones keep a readable text column.
            checklist.addView(ui.listRow(step.label, step.detail, badge, below = action).indent())
        }
    }

    private fun renderPc(s: SyncEngine.Status) {
        pcSection.removeAllViews()
        if (s.paired.isEmpty()) {
            pcSection.addView(ui.listRow("No PC paired", "Open Midfielder on your PC and choose Pair a phone", ui.iconBadge(R.drawable.ic_laptop, 40)))
            pcSection.addView(ui.button("Scan pairing code", Ui.Button.FILLED, R.drawable.ic_qr) { host.startScan() }, ui.matchWidth(8))
            return
        }
        for (name in s.paired) {
            val online = name in s.connected
            pcSection.addView(ui.listRow(name, if (online) "Connected" else "Not connected right now",
                ui.iconBadge(R.drawable.ic_laptop, 40, if (online) ui.primary else ui.secondaryContainer, if (online) ui.onPrimary else ui.onSecondaryContainer)))
        }
        val buttons = ui.horizontal()
        buttons.addView(ui.button("Pair again", Ui.Button.OUTLINED, R.drawable.ic_qr) { host.startScan() }, ui.weight())
        buttons.addView(
            ui.button("Forget", Ui.Button.OUTLINED, R.drawable.ic_delete) {
                host.confirm("Forget paired PC?", "You will need to scan a new code to connect again.", "Forget") { SyncEngine.current?.unpairAll() }
            }.apply {
                setTextColor(ui.errorColor)
                iconTint = android.content.res.ColorStateList.valueOf(ui.errorColor)
            },
            ui.weight(1f, 12),
        )
        pcSection.addView(buttons, ui.matchWidth(8))
    }
}
