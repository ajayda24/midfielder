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
        pageRoot = root
        root.addView(ui.headline("Settings").apply { setPadding(ui.dp(4), ui.dp(12), 0, ui.dp(4)) })

        root.addView(ui.sectionHeader("Sync"))
        val sync = ui.card(paddingDp = 8)
        sync.body.addView(ui.switchRow("Sync clipboard", "Copy on one device, paste on the other", storage.syncClipboard) { on ->
            storage.syncClipboard = on
            SyncEngine.current?.clipSync?.enabled = on
        }.indent())
        sync.body.addView(ui.divider())
        sync.body.addView(ui.switchRow("Accept files from your PC", "Saved to Download/Midfielder. Off: files from the PC are refused", storage.acceptFiles) {
            storage.acceptFiles = it
        }.indent())
        root.addView(sync.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("Features"))
        root.addView(ui.small("Each feature works when it's on here and in Midfielder on your PC. Off means it doesn't run at all.").apply {
            setPadding(ui.dp(4), 0, ui.dp(4), ui.dp(4))
        })
        featuresBox = ui.vertical()
        root.addView(featuresBox, ui.matchWidth(0))
        buildFeatures()

        setupHeader = ui.sectionHeader("Automatic copy setup")
        root.addView(setupHeader)
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

        root.addView(ui.sectionHeader("Advanced"))
        val advanced = ui.card(paddingDp = 8)
        advanced.body.addView(
            ui.listRow("Developer", "Connection details, the event log and debugging tools", ui.iconBadge(R.drawable.ic_code, 40),
                ui.icon(R.drawable.ic_chevron_right, ui.onSurfaceVariant)) {
                host.showTab(MainActivity.TAB_DEVELOPER)
            }.indent(),
        )
        root.addView(advanced.card, ui.matchWidth(4))

        val tip = ui.card(Ui.Card.FILLED, fill = ui.secondaryContainer)
        tip.card.cardElevation = 0f
        tip.body.addView(ui.text("Tip", com.google.android.material.R.style.TextAppearance_Material3_LabelLarge, ui.onSecondaryContainer))
        tip.body.addView(ui.text(
            "Add the \"Send clipboard\" tile to Quick Settings for a one-tap send, and pick your PC in the top row of the share sheet to send files.",
            com.google.android.material.R.style.TextAppearance_Material3_BodyMedium, ui.onSecondaryContainer,
        ).apply { setPadding(0, ui.dp(4), 0, 0) })
        root.addView(tip.card, ui.matchWidth(20))
        scroll = ScrollView(host).apply { addView(root) }
        return scroll
    }

    private lateinit var scroll: ScrollView
    private lateinit var setupHeader: View

    /** Home's "Finish setup" banner lands here: show the checklist, not the top of Settings. */
    fun scrollToSetup() {
        if (!::scroll.isInitialized) return
        scroll.post { scroll.smoothScrollTo(0, (setupHeader.top - ui.dp(8)).coerceAtLeast(0)) }
    }

    private fun LinearLayout.indent(): LinearLayout = apply { setPadding(ui.dp(12), paddingTop, ui.dp(12), paddingBottom) }

    override fun start(scope: CoroutineScope) {
        renderChecklist()
        scope.launch { engine().status.collect { renderPc(it) } }
        // Feature cards say whether the PC has each feature on too: text updates in place, nothing is rebuilt.
        scope.launch { engine().features.pc.collect { refresh(animate = false) } }
        scope.launch { engine().features.beacon.problem.collect { refresh(animate = false) } }
    }

    /**
     * One feature: icon, title, description, a status line and the switch, with the feature's options inside the
     * same card. Built once; switching only slides the options open or closed and updates text in place, so the
     * rest of the screen never redraws (no blink, no jumping).
     */
    private inner class FeatureCard(
        icon: Int,
        title: String,
        description: String,
        val capability: String,
        val isOn: () -> Boolean,
        set: (Boolean) -> Unit,
    ) {
        val card = ui.card(Ui.Card.FILLED, paddingDp = 0)
        private val badge = ui.iconBadge(icon, 44, ui.secondaryContainer, ui.onSecondaryContainer, cornerDp = 14)
        val status = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_LabelMedium, ui.onSurfaceVariant).apply {
            setPadding(0, ui.dp(4), 0, 0)
        }
        private val divider = ui.divider()
        val options = ui.vertical().apply { setPadding(ui.dp(16), 0, ui.dp(12), ui.dp(8)) }

        init {
            val header = ui.horizontal().apply { setPadding(ui.dp(16), ui.dp(14), ui.dp(12), ui.dp(14)) }
            header.addView(badge)
            val texts = ui.vertical().apply { setPadding(ui.dp(14), 0, ui.dp(8), 0) }
            texts.addView(ui.title(title))
            texts.addView(ui.supporting(description))
            texts.addView(status)
            header.addView(texts, ui.weight())
            header.addView(ui.switch(isOn()) { v ->
                set(v)
                onFeatureSwitched(this, v)
            }.apply { contentDescription = title }, ui.wrap())
            card.body.addView(header)
            card.body.addView(divider)
            card.body.addView(options)
            featuresBox.addView(card.card, ui.matchWidth(10))
        }

        /** Options show only while the feature is on, and only if it has any. */
        fun showOptions() {
            val on = isOn()
            val any = (0 until options.childCount).any { options.getChildAt(it).visibility == View.VISIBLE }
            divider.visibility = if (on && any) View.VISIBLE else View.GONE
            options.visibility = if (on && any) View.VISIBLE else View.GONE
            (badge.background as? android.graphics.drawable.GradientDrawable)?.setColor(if (on) ui.primaryContainer else ui.secondaryContainer)
            ((badge as? android.view.ViewGroup)?.getChildAt(0) as? android.widget.ImageView)?.imageTintList =
                android.content.res.ColorStateList.valueOf(if (on) ui.onPrimaryContainer else ui.onSecondaryContainer)
        }
    }

    private val cards = mutableListOf<FeatureCard>()
    private lateinit var pageRoot: LinearLayout

    /** Views inside the cards whose visibility or text depends on state (permissions, nested options, the PC). */
    private val dynamic = mutableListOf<() -> Unit>()

    /** Builds the feature cards once. */
    private fun buildFeatures() {
        cards.clear()
        dynamic.clear()
        featuresBox.removeAllViews()
        fun card(icon: Int, title: String, description: String, capability: String, isOn: () -> Boolean, set: (Boolean) -> Unit) =
            FeatureCard(icon, title, description, capability, isOn, set).also { cards += it }
        fun option(c: FeatureCard, title: String, subtitle: String, on: Boolean, perms: () -> List<String> = { emptyList() }, set: (Boolean) -> Unit): View {
            val row = ui.switchRow(title, subtitle, on) {
                set(it)
                optionChanged(if (it) perms() else emptyList())
            }
            c.options.addView(row)
            return row
        }
        /** A row shown only while [visible]; [text] (if given) is refreshed with the rest. */
        fun conditional(c: FeatureCard, view: View, visible: () -> Boolean) {
            c.options.addView(view)
            dynamic += { view.visibility = if (visible()) View.VISIBLE else View.GONE }
        }
        fun prompt(why: String, label: String, onClick: () -> Unit) = ui.listRow(
            "Permission needed", why,
            ui.iconBadge(R.drawable.ic_warning, 40, ui.errorContainer, ui.onErrorContainer),
            below = ui.button(label, Ui.Button.TONAL) { onClick() },
        )
        val notificationAccess = { !PhoneNotificationListener.isGranted(host) }

        val notify = card(R.drawable.ic_notifications, "Notifications on PC", "See and reply to this phone's notifications on your PC",
            Features.NOTIFY, { storage.featNotify }) { storage.featNotify = it }
        conditional(notify, prompt("Needed to read this phone's notifications", "Allow") { openNotificationAccess() }) { notificationAccess() }
        option(notify, "Reply and actions from PC", "Answer messages and tap notification buttons", storage.notifyReply) { storage.notifyReply = it }

        val battery = card(R.drawable.ic_battery, "Battery sharing", "Your PC's battery here, this phone's on the PC",
            Features.BATTERY, { storage.featBattery }) { storage.featBattery = it }
        option(battery, "PC battery alerts", "When the PC is charged or low", storage.batteryAlerts) { storage.batteryAlerts = it }
        // A label above short chips: label and chips in one row don't fit a narrow phone.
        // One choice, always one selected: TalkBack reads it as a group of options.
        val chips = com.google.android.material.chip.ChipGroup(host).apply {
            isSingleSelection = true
            isSelectionRequired = true
            chipSpacingHorizontal = ui.dp(8)
            setPadding(ui.dp(4), 0, 0, ui.dp(8))
        }
        conditional(battery, ui.supporting("Charged alert at").apply { setPadding(ui.dp(4), ui.dp(4), 0, ui.dp(4)) }) { storage.batteryAlerts }
        val levels = listOf(80, 90, 100).map { level ->
            ui.filterChip("$level%", storage.batteryFullAt == level).apply {
                setOnClickListener {
                    storage.batteryFullAt = level
                    SyncEngine.current?.features?.apply()
                    refresh()
                }
            }.also {
                it.id = View.generateViewId()
                chips.addView(it)
            }
        }
        dynamic += { levels.forEachIndexed { i, chip -> chip.isChecked = storage.batteryFullAt == listOf(80, 90, 100)[i] } }
        conditional(battery, chips) { storage.batteryAlerts }

        val ring = card(R.drawable.ic_ring, "Find my phone", "Your PC can ring this phone at full volume, even on silent",
            Features.RING, { storage.featRing }) { storage.featRing = it }
        val ringPc = ui.listRow("Ring my PC", "Plays a loud sound on your PC so you can find it", trailing = ui.button("Ring", Ui.Button.TONAL, R.drawable.ic_ring) {
            val sent = SyncEngine.current?.features?.ringPc() == true
            host.snack(if (sent) "Ringing your PC" else "Your PC can't ring right now")
        })
        conditional(ring, ringPc) { SyncEngine.current?.features?.pc?.value?.canRing == true }
        val ringHint = ui.supporting("Turn on Find my phone in Midfielder on your PC too, and it can be rung from here").apply {
            setPadding(ui.dp(4), ui.dp(10), 0, ui.dp(10))
        }
        conditional(ring, ringHint) { SyncEngine.current?.features?.pc?.value?.canRing != true }

        val music = card(R.drawable.ic_music, "Music control", "Play, pause and skip across your phone and PC",
            Features.MEDIA, { storage.featMedia }) { storage.featMedia = it }
        option(music, "Phone music on PC", "In the Windows media controls and media keys", storage.mediaPhoneToPc) {
            storage.mediaPhoneToPc = it
            if (it && notificationAccess()) openNotificationAccess()
        }
        conditional(music, prompt("Phone music on PC needs notification access", "Allow") { openNotificationAccess() }) {
            storage.mediaPhoneToPc && notificationAccess()
        }
        option(music, "PC music on phone", "In this phone's media controls and lock screen", storage.mediaPcToPhone) { storage.mediaPcToPhone = it }

        val audio = card(R.drawable.ic_headphones, "Audio", "Hear this phone on your PC's speakers, or your PC on this phone",
            Features.AUDIO, { storage.featAudio }) { storage.featAudio = it }
        option(audio, "Phone audio on PC", "Your PC becomes this phone's Bluetooth speaker. Pair them over Bluetooth once", storage.audioPhoneToPc) { storage.audioPhoneToPc = it }
        option(audio, "PC audio on phone", "Everything your PC plays, over Wi-Fi", storage.audioPcToPhone) { storage.audioPcToPhone = it }

        val calls = card(R.drawable.ic_call, "Calls on PC", "See who's calling on your PC, answer or decline from there",
            Features.CALL, { storage.featCalls }) { storage.featCalls = it }
        conditional(calls, prompt("Phone and call log access shows who's calling", "Grant") { grant() }) {
            dev.midfielder.android.CallBridge.missing(host, storage).isNotEmpty()
        }
        option(calls, "Answer and decline from PC", "Buttons in the PC's call popup", storage.callsActions,
            { listOf(android.Manifest.permission.ANSWER_PHONE_CALLS) }) { storage.callsActions = it }
        option(calls, "Contact names and photos", "Looked up on this phone, only for the caller", storage.callsContacts,
            { listOf(android.Manifest.permission.READ_CONTACTS) }) { storage.callsContacts = it }
        option(calls, "Quick text reply", "Decline with a short message from the PC", storage.callsSms,
            { listOf(android.Manifest.permission.SEND_SMS) }) { storage.callsSms = it }
        val smsText = ui.supporting(storage.callsSmsText)
        val sms = ui.horizontal().apply { setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(8)) }
        sms.addView(ui.vertical().apply {
            addView(ui.text("Message", com.google.android.material.R.style.TextAppearance_Material3_BodyLarge, ui.onSurface))
            addView(smsText)
        }, ui.weight())
        sms.addView(ui.button("Edit", Ui.Button.TEXT) { editSmsText() }, ui.wrap(8))
        conditional(calls, sms) { storage.callsSms }
        dynamic += { smsText.text = storage.callsSmsText }

        val lock = card(R.drawable.ic_lock, "Lock PC when I walk away", "A tiny Bluetooth signal tells your PC you're near; it locks when you leave",
            Features.PRESENCE, { storage.featPresence }) { storage.featPresence = it }
        conditional(lock, prompt("\"Nearby devices\" lets Midfielder send the Bluetooth signal", "Grant") { grant() }) {
            !dev.midfielder.android.PresenceBeacon.canAdvertise(host)
        }
        val beaconState = ui.supporting("").apply { setPadding(ui.dp(4), ui.dp(10), 0, ui.dp(10)) }
        conditional(lock, beaconState) { dev.midfielder.android.PresenceBeacon.canAdvertise(host) }
        dynamic += {
            val f = SyncEngine.current?.features
            val problem = f?.beacon?.problem?.value
            beaconState.text = when {
                f == null || !f.pc.value.presenceReady -> "Waiting for your PC: turn on the same feature in Midfielder on the PC"
                problem != null -> problem
                else -> "Beacon on. Set how far is \"away\" on your PC"
            }
        }
        refresh(animate = false)
    }

    /** Updates status lines, prompts and option visibility in place (never rebuilds). */
    private fun refresh(animate: Boolean = true) {
        if (!::featuresBox.isInitialized || cards.isEmpty()) return
        if (animate && ::pageRoot.isInitialized) {
            android.transition.TransitionManager.beginDelayedTransition(
                pageRoot,
                android.transition.TransitionSet()
                    .addTransition(android.transition.ChangeBounds())
                    .addTransition(android.transition.Fade())
                    .setOrdering(android.transition.TransitionSet.ORDERING_TOGETHER)
                    .setDuration(180),
            )
        }
        dynamic.forEach { it() }
        val pc = SyncEngine.current?.features?.pc?.value
        for (c in cards) {
            val on = c.isOn()
            val needs = needs(c.capability)
            val (text, color) = when {
                !on -> "Off" to ui.onSurfaceVariant
                needs != null -> needs to ui.errorColor
                pc == null || pc.name == null -> "On · works when your PC is connected" to ui.onSurfaceVariant
                c.capability !in pc.capabilities -> "Turn it on in Midfielder on your PC too" to ui.errorColor
                else -> "On · working with ${pc.name}" to ui.primary
            }
            if (c.status.text != text) c.status.text = text
            c.status.setTextColor(color)
            c.showOptions()
        }
    }

    /** What a switched-on feature still needs from Android before it can work, if anything. */
    private fun needs(capability: String): String? = when (capability) {
        Features.NOTIFY -> "Needs notification access".takeIf { !PhoneNotificationListener.isGranted(host) }
        Features.MEDIA -> "Phone music needs notification access".takeIf { storage.mediaPhoneToPc && !PhoneNotificationListener.isGranted(host) }
        Features.CALL -> "Needs permission".takeIf { dev.midfielder.android.CallBridge.missing(host, storage).isNotEmpty() }
        Features.PRESENCE -> "Needs Nearby devices".takeIf { !dev.midfielder.android.PresenceBeacon.canAdvertise(host) }
        else -> null
    }

    /** A feature switch was flipped: apply it, ask for what it needs now (only when turned on), update in place. */
    private fun onFeatureSwitched(card: FeatureCard, on: Boolean) {
        SyncEngine.current?.features?.apply()
        if (on) {
            when (card.capability) {
                Features.NOTIFY -> if (!PhoneNotificationListener.isGranted(host)) openNotificationAccess()
                Features.MEDIA -> if (storage.mediaPhoneToPc && !PhoneNotificationListener.isGranted(host)) openNotificationAccess()
                Features.CALL -> ask(dev.midfielder.android.CallBridge.missing(host, storage))
                Features.PRESENCE -> ask(dev.midfielder.android.PresenceBeacon.permissions)
            }
        }
        refresh()
    }

    private fun optionChanged(needs: List<String>) {
        SyncEngine.current?.features?.apply()
        ask(needs)
        refresh()
    }

    /** Asks for the [permissions] still missing (only right after a switch was turned on); nothing if all are granted. */
    private fun ask(permissions: List<String>) {
        val missing = permissions.filterNot { dev.midfielder.android.CallBridge.granted(host, it) }
            .filterNot { dev.midfielder.android.Permissions.blocked(host, it) } // the card's Grant row handles those
        if (missing.isNotEmpty()) dev.midfielder.android.Permissions.request(host, missing.toTypedArray(), MainActivity.REQ_FEATURES)
    }

    /**
     * The Grant button: asks again, or, once Android has stopped showing the prompt ("don't ask again"), opens
     * Midfielder's page in Android settings where the permission can be turned on.
     */
    private fun grant() {
        dev.midfielder.android.Permissions.requestOrOpenSettings(host, runtimePermissionsMissing(), MainActivity.REQ_FEATURES)
    }

    /** Runtime permissions the switched-on features still need. */
    private fun runtimePermissionsMissing(): List<String> = buildList {
        addAll(dev.midfielder.android.CallBridge.missing(host, storage))
        if (storage.featPresence) addAll(dev.midfielder.android.PresenceBeacon.permissions.filterNot { dev.midfielder.android.CallBridge.granted(host, it) })
    }

    /** A permission answer came back: start what can start now and redraw. */
    fun onPermissionsResult() {
        SyncEngine.current?.features?.apply()
        refresh()
    }

    private fun editSmsText() {
        val layout = com.google.android.material.textfield.TextInputLayout(host).apply {
            hint = "Message"
            isCounterEnabled = true
            counterMaxLength = SMS_CHARS
            helperText = "Sent to the caller when you choose Text back on the PC"
        }
        val input = com.google.android.material.textfield.TextInputEditText(layout.context).apply {
            setText(storage.callsSmsText)
            setSelection(text?.length ?: 0)
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES or
                android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE
            maxLines = 4
        }
        layout.addView(input)
        val box = android.widget.FrameLayout(host).apply {
            setPadding(ui.dp(24), ui.dp(8), ui.dp(24), 0)
            addView(layout)
        }
        val dialog = com.google.android.material.dialog.MaterialAlertDialogBuilder(host)
            .setTitle("Quick text reply")
            .setView(box)
            .setPositiveButton("Save", null)
            .setNegativeButton("Cancel", null)
            .show()
        // Validate before closing: an empty message isn't saved silently.
        dialog.getButton(android.content.DialogInterface.BUTTON_POSITIVE).setOnClickListener {
            val t = input.text?.toString()?.trim().orEmpty()
            if (t.isEmpty()) {
                layout.error = "Write a short message"
                return@setOnClickListener
            }
            storage.callsSmsText = t
            SyncEngine.current?.features?.apply()
            refresh()
            dialog.dismiss()
        }
    }

    /**
     * Explains first, then opens Android's notification-access page for Midfielder. The listener component is
     * enabled before, or the page can't find Midfielder. On Android 13+ a sideloaded app's toggle is greyed out
     * until "Allow restricted settings" is chosen in App info, so that's said too.
     */
    private fun openNotificationAccess() {
        PhoneNotificationListener.setEnabled(host, true)
        val restricted = dev.midfielder.android.Permissions.restrictedSettingsHint?.let { "\n\n$it" }.orEmpty()
        com.google.android.material.dialog.MaterialAlertDialogBuilder(host)
            .setTitle("Allow notification access")
            .setMessage("Midfielder needs it to show this phone's notifications and music on your PC. Nothing leaves your own network.$restricted")
            .setPositiveButton("Open settings") { _, _ -> openNotificationAccessPage() }
            .setNeutralButton("App info") { _, _ -> dev.midfielder.android.Permissions.openAppSettings(host) }
            .setNegativeButton("Not now", null)
            .show()
    }

    private fun openNotificationAccessPage() {
        val intent = if (android.os.Build.VERSION.SDK_INT >= 30) {
            android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                .putExtra(
                    android.provider.Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                    PhoneNotificationListener.component(host).flattenToString(),
                )
        } else {
            android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)
        }
        dev.midfielder.android.Permissions.openSafely(host, intent, fallback = android.content.Intent(android.provider.Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
    }

    fun renderChecklist() {
        refresh(animate = false) // permissions or notification access may have changed
        if (!::checklist.isInitialized) return
        checklist.removeAllViews()
        Setup.steps(host).forEachIndexed { i, step ->
            if (i > 0) checklist.addView(ui.divider())
            val badge = if (step.done) ui.iconBadge(R.drawable.ic_check, 40, ui.primaryContainer, ui.onPrimaryContainer)
            else ui.iconBadge(R.drawable.ic_warning, 40, ui.errorContainer, ui.onErrorContainer)
            val action = if (step.done) null else ui.button(step.action, Ui.Button.TONAL) {
                runCatching { step.run(host) }.onFailure { host.snack("Couldn't open that setting on this phone") }
            }
            // The action goes under the text so narrow phones keep a readable text column.
            checklist.addView(ui.listRow(step.label, step.detail, badge, below = action).indent())
        }
    }

    private var renderedPc: Pair<List<String>, List<String>>? = null

    private fun renderPc(s: SyncEngine.Status) {
        val key = s.paired to s.connected
        if (key == renderedPc) return // status changes after every event: rebuild only when the PCs changed
        renderedPc = key
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
        val many = s.paired.size > 1
        val pairAgain = ui.button("Pair again", Ui.Button.OUTLINED, R.drawable.ic_qr) { host.startScan() }
        val forget = ui.button(if (many) "Forget all" else "Forget", Ui.Button.OUTLINED, R.drawable.ic_delete) {
            host.confirm(
                if (many) "Forget all ${s.paired.size} paired PCs?" else "Forget ${s.paired.first()}?",
                "You will need to scan a new code to connect again.", "Forget",
            ) { SyncEngine.current?.unpairAll() }
        }.apply {
            setTextColor(ui.errorColor)
            iconTint = android.content.res.ColorStateList.valueOf(ui.errorColor)
        }
        // Side by side when there's room; stacked on narrow phones or with large text.
        val narrow = host.resources.configuration.screenWidthDp < 360 || host.resources.configuration.fontScale > 1.3f
        if (narrow) {
            pcSection.addView(pairAgain, ui.matchWidth(8))
            pcSection.addView(forget, ui.matchWidth(8))
        } else {
            val buttons = ui.horizontal()
            buttons.addView(pairAgain, ui.weight())
            buttons.addView(forget, ui.weight(1f, 12))
            pcSection.addView(buttons, ui.matchWidth(8))
        }
    }

    private companion object {
        /** One SMS; longer replies are split into parts, which costs more on some plans. */
        const val SMS_CHARS = 160
    }
}
