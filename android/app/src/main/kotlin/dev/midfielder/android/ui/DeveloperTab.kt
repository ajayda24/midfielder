package dev.midfielder.android.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import dev.midfielder.android.EventLog
import dev.midfielder.android.MainActivity
import dev.midfielder.android.R
import dev.midfielder.android.Setup
import dev.midfielder.android.SyncEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** Diagnostics, ADB commands and the activity log: everything that would clutter the main screens. */
class DeveloperTab(host: MainActivity) : Tab(host) {
    private lateinit var diag: LinearLayout
    private var entries: List<EventLog.Entry> = emptyList()

    private class Holder(view: View) : RecyclerView.ViewHolder(view)

    private val adapter = object : RecyclerView.Adapter<Holder>() {
        override fun getItemCount() = entries.size + HEADER_ITEMS

        override fun getItemViewType(position: Int) = if (position < HEADER_ITEMS) position else HEADER_ITEMS

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder = Holder(
            when (viewType) {
                0 -> header()
                else -> LogRow()
            },
        )

        override fun onBindViewHolder(holder: Holder, position: Int) {
            if (position >= HEADER_ITEMS) (holder.itemView as LogRow).bind(entries[position - HEADER_ITEMS], position == itemCount - 1)
        }
    }

    override fun build(): View = RecyclerView(host).apply {
        layoutManager = LinearLayoutManager(host)
        adapter = this@DeveloperTab.adapter
        clipToPadding = false
        setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(24))
    }

    /** Everything above the log, as the list's first item (so the whole page scrolls as one). */
    private fun header(): View {
        val root = ui.vertical().apply {
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        }
        root.addView(ui.headline("Developer").apply { setPadding(ui.dp(4), ui.dp(12), 0, ui.dp(2)) })
        root.addView(ui.supporting("Diagnostics for when something doesn't sync").apply { setPadding(ui.dp(4), 0, 0, 0) })

        root.addView(ui.sectionHeader("Status"))
        val status = ui.card()
        diag = status.body
        root.addView(status.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("ADB commands"))
        val adb = ui.card()
        adb.body.addView(command("Silent copy detection", Setup.ADB_GRANT))
        adb.body.addView(command("Live log on your PC", Setup.ADB_LOGS).apply { setPadding(0, ui.dp(12), 0, 0) })
        root.addView(adb.card, ui.matchWidth(4))

        root.addView(ui.sectionHeader("Activity log"))
        val chips = ui.horizontal()
        chips.addView(ui.assistChip("Copy", R.drawable.ic_copy) {
            Setup.copy(host, logText())
            host.snack("Log copied")
        })
        chips.addView(ui.assistChip("Share", R.drawable.ic_share) {
            runCatching {
                host.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, logText()), null))
            }
        }, ui.wrap(8))
        chips.addView(ui.assistChip("Clear", R.drawable.ic_delete) { EventLog.clear() }, ui.wrap(8))
        root.addView(chips)
        if (::diag.isInitialized) SyncEngine.current?.let(::renderDiag)
        return root
    }

    override fun start(scope: CoroutineScope) {
        scope.launch {
            val engine = engine()
            launch { engine.status.collect { if (::diag.isInitialized) renderDiag(engine) } }
        }
        scope.launch {
            EventLog.entries.collect {
                entries = it.take(MAX_SHOWN)
                adapter.notifyDataSetChanged()
            }
        }
    }

    private fun command(label: String, cmd: String): View = ui.vertical().apply {
        addView(ui.small(label))
        addView(mono(cmd, ui.onSecondaryContainer).apply {
            background = ui.pill(ui.secondaryContainer, 10)
            setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10))
        }, ui.matchWidth(6))
        isClickable = true
        setOnClickListener {
            Setup.copy(host, cmd)
            host.snack("Command copied")
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
            val row = ui.horizontal().apply {
                gravity = Gravity.TOP
                setPadding(0, ui.dp(6), 0, ui.dp(6))
            }
            row.addView(ui.small(k), LinearLayout.LayoutParams(ui.dp(116), ViewGroup.LayoutParams.WRAP_CONTENT))
            row.addView(mono(v, ui.onSurface).apply { setTextIsSelectable(true) }, ui.weight())
            diag.addView(row)
        }
    }

    private inner class LogRow : LinearLayout(host) {
        private val stripe = View(host)
        private val time = mono("", ui.onSurfaceVariant)
        private val message = mono("", ui.onSurface)

        init {
            orientation = HORIZONTAL
            setPadding(ui.dp(12), ui.dp(8), ui.dp(12), ui.dp(8))
            setBackgroundColor(ui.containerLowest)
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            addView(stripe, LayoutParams(ui.dp(3), ViewGroup.LayoutParams.MATCH_PARENT).apply { marginEnd = ui.dp(10) })
            addView(time, LayoutParams(ui.dp(66), ViewGroup.LayoutParams.WRAP_CONTENT))
            addView(message, LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }

        fun bind(e: EventLog.Entry, last: Boolean) {
            time.text = e.time
            message.text = e.message
            message.setTextColor(if (e.isError) ui.errorColor else ui.onSurface)
            stripe.setBackgroundColor(if (e.isError) ui.errorColor else ui.secondaryContainer)
            setBackgroundColor(if (e.isError) ui.errorContainer else ui.containerLowest)
            (layoutParams as RecyclerView.LayoutParams).bottomMargin = if (last) 0 else ui.dp(1)
        }
    }

    private fun mono(s: String, color: Int): TextView = ui.small(s).apply {
        typeface = Typeface.MONOSPACE
        setTextColor(color)
    }

    private fun logText() = EventLog.entries.value.joinToString("\n")

    private companion object {
        const val MAX_SHOWN = 150
        const val HEADER_ITEMS = 1
    }
}
