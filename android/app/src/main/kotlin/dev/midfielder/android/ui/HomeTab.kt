package dev.midfielder.android.ui

import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import dev.midfielder.android.EventLog
import dev.midfielder.android.MainActivity
import dev.midfielder.android.R
import dev.midfielder.android.Setup
import dev.midfielder.android.SyncEngine
import dev.midfielder.core.history.ActiveTransfer
import dev.midfielder.core.history.Direction
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.format.TextStyle
import java.util.Locale

/** Connection status, the two big send actions, live transfers and a small activity summary. */
class HomeTab(host: MainActivity) : Tab(host) {
    private lateinit var pcName: TextView
    private lateinit var pcStatus: TextView
    private lateinit var statusDot: View
    private lateinit var capture: TextView
    private lateinit var pairButton: View
    private lateinit var transfersCard: LinearLayout
    private lateinit var transfersList: LinearLayout
    private lateinit var setupCard: LinearLayout
    private lateinit var setupText: TextView
    private lateinit var statClips: TextView
    private lateinit var statFiles: TextView
    private lateinit var statData: TextView
    private lateinit var weekText: TextView
    private lateinit var chart: BarChart

    override fun build(): View {
        val root = ui.vertical().apply { setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(24)) }
        root.addView(ui.title("Midfielder"))
        root.addView(ui.subtitle("Your clipboard and files, on phone and PC"))

        // Connection
        val conn = ui.card()
        val head = ui.horizontal()
        head.addView(ui.iconBadge(R.drawable.ic_laptop, 48))
        val names = ui.vertical().apply { setPadding(ui.dp(14), 0, 0, 0) }
        pcName = ui.text("Starting…", 18f, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END }
        names.addView(pcName)
        val statusRow = ui.horizontal()
        statusDot = ui.dot(Palette.MUTED)
        statusRow.addView(statusDot)
        pcStatus = ui.text("", 14f, Palette.MUTED)
        statusRow.addView(pcStatus)
        names.addView(statusRow)
        head.addView(names, ui.weight())
        conn.addView(head)
        capture = ui.text("", 13f, Palette.MUTED).apply { setPadding(0, ui.dp(12), 0, 0) }
        conn.addView(capture)
        pairButton = ui.primaryButton("Pair with your PC", R.drawable.ic_qr) { host.startScan() }
        conn.addView(pairButton, ui.matchWidth(14))
        root.addView(conn, ui.matchWidth(4))

        // Quick actions
        val actions = ui.horizontal()
        actions.addView(actionTile(R.drawable.ic_copy, "Send clipboard", "Copy on PC instantly") { host.sendClipboardNow() }, ui.weight())
        actions.addView(actionTile(R.drawable.ic_upload, "Send files", "Photos, docs, anything") { host.pickFiles() }, ui.weight(1f, 12))
        root.addView(actions, ui.matchWidth())

        // Live transfers
        transfersCard = ui.card().apply { visibility = View.GONE }
        transfersCard.addView(ui.text("Transfers", 16f, bold = true))
        transfersList = ui.vertical()
        transfersCard.addView(transfersList)
        root.addView(transfersCard, ui.matchWidth())

        // Setup nudge (only while something is missing)
        setupCard = ui.horizontal().apply {
            background = ui.pressable(ui.rounded(Palette.HIGHLIGHT, 16), 16)
            setPadding(ui.dp(16), ui.dp(14), ui.dp(12), ui.dp(14))
            setOnClickListener { host.showTab(MainActivity.TAB_SETTINGS) }
            visibility = View.GONE
        }
        setupText = ui.text("", 14f, Palette.PRIMARY_DARK, bold = true)
        setupCard.addView(setupText, ui.weight())
        setupCard.addView(ui.text("›", 22f, Palette.PRIMARY_DARK))
        root.addView(setupCard, ui.matchWidth())

        // Activity summary
        val stats = ui.card()
        stats.addView(ui.text("Today", 16f, bold = true))
        val nums = ui.horizontal().apply { setPadding(0, ui.dp(8), 0, ui.dp(8)) }
        statClips = stat(nums, "clips")
        statFiles = stat(nums, "files")
        statData = stat(nums, "moved")
        stats.addView(nums)
        chart = BarChart(host)
        stats.addView(chart, ui.matchWidth(4))
        weekText = ui.text("", 13f, Palette.MUTED).apply { setPadding(0, ui.dp(8), 0, 0) }
        stats.addView(weekText)
        root.addView(stats, ui.matchWidth())

        return ScrollView(host).apply {
            isFillViewport = true
            addView(root)
        }
    }

    private fun actionTile(icon: Int, title: String, sub: String, onClick: () -> Unit): View = ui.vertical().apply {
        background = ui.pressable(ui.rounded(Palette.SURFACE, 16, Palette.OUTLINE), 16)
        setPadding(ui.dp(16), ui.dp(16), ui.dp(16), ui.dp(16))
        addView(ui.iconBadge(icon, 40))
        addView(ui.text(title, 16f, bold = true).apply { setPadding(0, ui.dp(12), 0, 0) })
        addView(ui.text(sub, 12f, Palette.MUTED))
        isClickable = true
        setOnClickListener { onClick() }
    }

    private fun stat(parent: LinearLayout, label: String): TextView {
        val col = ui.vertical()
        val value = ui.text("0", 22f, Palette.PRIMARY_DARK, bold = true)
        col.addView(value)
        col.addView(ui.text(label, 12f, Palette.MUTED))
        parent.addView(col, ui.weight())
        return value
    }

    override fun start(scope: CoroutineScope) {
        refreshSetup()
        scope.launch {
            val engine = engine()
            launch { combine(engine.status, EventLog.entries) { s, _ -> s }.collect { renderStatus(engine) } }
            launch { engine.transfers.active.collect(::renderTransfers) }
            launch { engine.history.version.collect { renderStats(engine) } }
        }
    }

    fun refreshSetup() {
        if (!::setupCard.isInitialized) return
        val steps = Setup.steps(host)
        val done = steps.count { it.done }
        setupCard.visibility = if (done == steps.size) View.GONE else View.VISIBLE
        setupText.text = "Finish setup for automatic copy · $done of ${steps.size} done"
    }

    private fun renderStatus(engine: SyncEngine) {
        val s = engine.status.value
        val connected = s.connected.isNotEmpty()
        val paired = s.paired.isNotEmpty()
        pcName.text = when {
            connected -> s.connected.joinToString()
            paired -> s.paired.joinToString()
            else -> "No PC paired yet"
        }
        pcStatus.text = when {
            connected -> "Connected"
            paired -> "Looking for your PC on this Wi-Fi…"
            else -> "Scan the QR code shown by Midfielder on your PC"
        }
        pcStatus.setTextColor(if (connected) Palette.PRIMARY else Palette.MUTED)
        (statusDot.background as? GradientDrawable)?.setColor(if (connected) 0xFF2E9B6F.toInt() else 0xFFB8C2BD.toInt())
        statusDot.visibility = if (paired) View.VISIBLE else View.GONE
        pairButton.visibility = if (paired) View.GONE else View.VISIBLE
        capture.text = "Phone → PC copy: ${engine.captureMode}"
        capture.visibility = if (paired) View.VISIBLE else View.GONE
    }

    private fun renderTransfers(list: List<ActiveTransfer>) {
        transfersCard.visibility = if (list.isEmpty()) View.GONE else View.VISIBLE
        transfersList.removeAllViews()
        for (t in list) transfersList.addView(transferRow(t))
    }

    private fun transferRow(t: ActiveTransfer): View {
        val box = ui.vertical().apply { setPadding(0, ui.dp(12), 0, 0) }
        val top = ui.horizontal()
        top.addView(ui.icon(if (t.direction == Direction.SENT) R.drawable.ic_upload else R.drawable.ic_download, Palette.PRIMARY, 20))
        val texts = ui.vertical().apply { setPadding(ui.dp(10), 0, 0, 0) }
        texts.addView(ui.text(t.title, 15f, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.MIDDLE })
        val pct = if (t.percent >= 0) " · ${t.percent}%" else ""
        val verb = if (t.direction == Direction.SENT) "to" else "from"
        texts.addView(ui.text("${Format.bytes(t.done)} of ${Format.bytes(t.total)} $verb ${t.peer}$pct", 12f, Palette.MUTED))
        top.addView(texts, ui.weight())
        top.addView(ui.iconButton(R.drawable.ic_close, "Cancel") { SyncEngine.current?.cancelTransfer(t.id) })
        box.addView(top)
        box.addView(ui.progressBar().apply {
            isIndeterminate = t.percent < 0
            progress = t.percent.coerceAtLeast(0)
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(6)).apply { topMargin = ui.dp(8) })
        return box
    }

    private fun renderStats(engine: SyncEngine) {
        val now = System.currentTimeMillis()
        val days = engine.history.stats.lastDays(7, now)
        val today = days.last()
        statClips.text = today.clips.toString()
        statFiles.text = today.files.toString()
        statData.text = Format.bytes(today.bytes)
        val labels = (6 downTo 0).map {
            LocalDate.now().minusDays(it.toLong()).dayOfWeek.getDisplayName(TextStyle.NARROW, Locale.getDefault())
        }
        chart.set(days.map { it.clips + it.files }, labels)
        val week = days.reduce { a, b -> a + b }
        weekText.text = "Last 7 days: ${week.clips} clips · ${week.files} files · ${Format.bytes(week.bytes)}"
    }
}
