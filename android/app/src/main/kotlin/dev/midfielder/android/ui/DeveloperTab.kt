package dev.midfielder.android.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import dev.midfielder.android.EventLog
import dev.midfielder.android.MainActivity
import dev.midfielder.android.Setup
import dev.midfielder.android.SyncEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** Diagnostics, the activity log and the ADB commands: everything that used to clutter the main screen. */
class DeveloperTab(host: MainActivity) : Tab(host) {
    private lateinit var diag: LinearLayout
    private lateinit var log: LinearLayout

    override fun build(): View {
        val root = ui.vertical().apply { setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(24)) }
        root.addView(ui.title("Developer"))
        root.addView(ui.subtitle("Diagnostics for when something doesn't sync"))

        root.addView(ui.sectionLabel("Status"))
        diag = ui.card()
        root.addView(diag)

        root.addView(ui.sectionLabel("ADB commands (tap to copy)"))
        val adb = ui.card()
        adb.addView(command("Silent copy detection", Setup.ADB_GRANT))
        adb.addView(ui.divider())
        adb.addView(command("Live log on your PC", Setup.ADB_LOGS))
        root.addView(adb)

        val logHeader = ui.horizontal().apply { setPadding(0, ui.dp(8), 0, 0) }
        logHeader.addView(ui.sectionLabel("Activity log"), ui.weight())
        logHeader.addView(ui.smallButton("Copy") {
            Setup.copy(host, logText())
            Toast.makeText(host, "Log copied", Toast.LENGTH_SHORT).show()
        })
        logHeader.addView(ui.smallButton("Share") {
            runCatching {
                host.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, logText()), null))
            }
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginStart = ui.dp(8) })
        logHeader.addView(ui.smallButton("Clear") { EventLog.clear() }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginStart = ui.dp(8) })
        root.addView(logHeader)
        log = ui.card().apply { setPadding(ui.dp(12), ui.dp(8), ui.dp(12), ui.dp(8)) }
        root.addView(log)
        return ScrollView(host).apply { addView(root) }
    }

    override fun start(scope: CoroutineScope) {
        scope.launch {
            val engine = engine()
            launch { engine.status.collect { renderDiag(engine) } }
        }
        scope.launch { EventLog.entries.collect(::renderLog) }
    }

    private fun command(label: String, cmd: String): View = ui.vertical().apply {
        setPadding(ui.dp(4), ui.dp(8), ui.dp(4), ui.dp(8))
        addView(ui.text(label, 14f, Palette.MUTED))
        addView(mono(cmd, Palette.PRIMARY_DARK).apply {
            background = ui.rounded(Palette.HIGHLIGHT, 8)
            setPadding(ui.dp(10), ui.dp(8), ui.dp(10), ui.dp(8))
        }, ui.matchWidth(6))
        background = ui.pressable(null, 12)
        setOnClickListener {
            Setup.copy(host, cmd)
            Toast.makeText(host, "Copied", Toast.LENGTH_SHORT).show()
        }
    }

    private fun renderDiag(engine: SyncEngine) {
        diag.removeAllViews()
        val s = engine.status.value
        val pkg = host.packageManager.getPackageInfo(host.packageName, 0)
        val readLogs = host.checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED
        val rows = listOf(
            "Version" to "${pkg.versionName} (${pkg.longVersionCode})",
            "Connected" to s.connected.joinToString().ifEmpty { "—" },
            "Paired" to s.paired.joinToString().ifEmpty { "—" },
            "Phone → PC copy" to engine.captureMode,
            "READ_LOGS" to if (readLogs) "granted" else "not granted",
            "Log reader" to if (engine.logDetectionActive) "approved, running" else "not active",
            "Accessibility" to if (Setup.isAccessibilityOn(host)) "on" else "off",
            "Device ID" to engine.deviceId,
            "Fingerprint" to engine.fingerprint,
        )
        for ((k, v) in rows) {
            val row = ui.horizontal().apply { setPadding(0, ui.dp(5), 0, ui.dp(5)) }
            row.addView(ui.text(k, 13f, Palette.MUTED), LinearLayout.LayoutParams(ui.dp(120), LinearLayout.LayoutParams.WRAP_CONTENT))
            row.addView(mono(v, Palette.TEXT).apply { setTextIsSelectable(true) }, ui.weight())
            diag.addView(row)
        }
    }

    private fun renderLog(entries: List<EventLog.Entry>) {
        log.removeAllViews()
        if (entries.isEmpty()) {
            log.addView(ui.text("Nothing logged yet", 13f, Palette.MUTED).apply { setPadding(0, ui.dp(8), 0, ui.dp(8)) })
            return
        }
        for (e in entries.take(MAX_SHOWN)) {
            val row = ui.horizontal().apply {
                setPadding(ui.dp(6), ui.dp(4), ui.dp(6), ui.dp(4))
                if (e.isError) background = ui.rounded(Palette.DANGER_BG, 6)
                gravity = android.view.Gravity.TOP
            }
            row.addView(mono(e.time, Palette.MUTED), LinearLayout.LayoutParams(ui.dp(64), LinearLayout.LayoutParams.WRAP_CONTENT))
            row.addView(mono(e.message, if (e.isError) Palette.DANGER else Palette.TEXT), ui.weight())
            log.addView(row)
        }
    }

    private fun mono(s: String, color: Int): TextView = ui.text(s, 12f, color).apply { typeface = Typeface.MONOSPACE }

    private fun logText() = EventLog.entries.value.joinToString("\n")

    private companion object {
        const val MAX_SHOWN = 100
    }
}
