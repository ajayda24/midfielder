package dev.midfielder.android.ui

import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import dev.midfielder.android.AppStorage
import dev.midfielder.android.FileActions
import dev.midfielder.android.HistoryStore
import dev.midfielder.android.MainActivity
import dev.midfielder.android.R
import dev.midfielder.android.Setup
import dev.midfielder.android.SyncEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** Sync switches, the automatic-copy checklist, the paired PC and storage. */
class SettingsTab(host: MainActivity) : Tab(host) {
    private lateinit var checklist: LinearLayout
    private lateinit var pcSection: LinearLayout

    override fun build(): View {
        val storage = AppStorage(host)
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
    }

    fun renderChecklist() {
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
            checklist.addView(ui.listRow(step.label, step.detail, badge, action).indent())
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
