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
        val root = ui.vertical().apply { setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(24)) }
        root.addView(ui.title("Settings"))

        root.addView(ui.sectionLabel("Sync"))
        val sync = ui.card()
        sync.addView(ui.switchRow("Sync clipboard", "Copy on one device, paste on the other", storage.syncClipboard) { on ->
            storage.syncClipboard = on
            SyncEngine.current?.clipSync?.enabled = on
        })
        sync.addView(ui.divider())
        sync.addView(ui.switchRow("Receive files automatically", "Saved to Download/Midfielder", storage.acceptFiles) {
            storage.acceptFiles = it
        })
        root.addView(sync)

        root.addView(ui.sectionLabel("Automatic copy setup"))
        checklist = ui.card()
        root.addView(checklist)

        root.addView(ui.sectionLabel("Paired PC"))
        pcSection = ui.card()
        root.addView(pcSection)

        root.addView(ui.sectionLabel("Storage"))
        val store = ui.card()
        store.addView(ui.row("Received files", "Download/Midfielder", ui.iconBadge(R.drawable.ic_folder, 36), ui.smallButton("Open") {
            FileActions.openFolder(host)
        }))
        store.addView(ui.divider())
        store.addView(ui.row("Reset statistics", "Clears the counts on Home", trailing = ui.smallButton("Reset") {
            host.confirm("Reset statistics?", "Clip and file counts start again from zero.", "Reset") { HistoryStore.get(host).clearStats() }
        }))
        root.addView(store)

        root.addView(ui.sectionLabel("Tip"))
        root.addView(ui.card().apply {
            addView(ui.text("Add the \"Send clipboard\" tile to Quick Settings for a one-tap send, and pick your PC at the top of the share sheet to send files.", 14f, Palette.MUTED))
        })
        return ScrollView(host).apply { addView(root) }
    }

    override fun start(scope: CoroutineScope) {
        renderChecklist()
        scope.launch { engine().status.collect { renderPc(it) } }
    }

    fun renderChecklist() {
        if (!::checklist.isInitialized) return
        checklist.removeAllViews()
        Setup.steps(host).forEachIndexed { i, step ->
            if (i > 0) checklist.addView(ui.divider())
            val badge = if (step.done) ui.iconBadge(R.drawable.ic_check, 32) else ui.iconBadge(R.drawable.ic_close, 32, Palette.DANGER_BG, Palette.DANGER)
            val action = if (step.done) null else ui.smallButton(step.action) {
                step.run(host)
                checklist.postDelayed({ renderChecklist() }, 3_000)
            }
            checklist.addView(ui.row(step.label, step.detail, badge, action))
        }
    }

    private fun renderPc(s: SyncEngine.Status) {
        pcSection.removeAllViews()
        if (s.paired.isEmpty()) {
            pcSection.addView(ui.row("No PC paired", "Open Midfielder on your PC and choose Pair a phone", ui.iconBadge(R.drawable.ic_laptop, 36)))
            pcSection.addView(ui.primaryButton("Scan pairing code", R.drawable.ic_qr) { host.startScan() }, ui.matchWidth(8))
            return
        }
        for (name in s.paired) {
            val online = name in s.connected
            pcSection.addView(ui.row(name, if (online) "Connected" else "Not connected right now", ui.iconBadge(R.drawable.ic_laptop, 36)))
        }
        val buttons = ui.horizontal()
        buttons.addView(ui.outlineButton("Pair again", R.drawable.ic_qr) { host.startScan() }, ui.weight())
        buttons.addView(
            ui.outlineButton("Forget", R.drawable.ic_delete, Palette.DANGER) {
                host.confirm("Forget paired PC?", "You will need to scan a new code to connect again.", "Forget") {
                    SyncEngine.current?.unpairAll()
                }
            },
            ui.weight(1f, 12),
        )
        pcSection.addView(buttons, ui.matchWidth(8))
    }
}
