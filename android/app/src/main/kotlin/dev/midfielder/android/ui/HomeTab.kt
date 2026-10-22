package dev.midfielder.android.ui

import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.GridLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.google.android.material.chip.Chip
import com.google.android.material.imageview.ShapeableImageView
import dev.midfielder.android.EventLog
import dev.midfielder.android.FileActions
import dev.midfielder.android.HistoryStore
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

/** Hero status card, quick actions, live transfers, recent files and a small activity summary. */
class HomeTab(host: MainActivity) : Tab(host) {
    private lateinit var pcName: TextView
    private lateinit var statusChip: Chip
    private lateinit var capture: TextView
    private lateinit var pairButton: MaterialButton
    private lateinit var pcBattery: TextView
    private lateinit var pcActions: LinearLayout
    private lateinit var ringTile: View
    private lateinit var pcAudioTile: View
    private lateinit var pcAudioTitle: TextView
    private lateinit var phoneAudioTile: View
    private val storage by lazy { dev.midfielder.android.AppStorage(host) }
    private lateinit var setupCard: MaterialCardView
    private lateinit var setupText: TextView
    private lateinit var transfersCard: MaterialCardView
    private lateinit var transfersList: LinearLayout
    private lateinit var recentHeader: View
    private lateinit var recentStrip: LinearLayout
    private lateinit var recentScroll: View
    private lateinit var statClips: TextView
    private lateinit var statFiles: TextView
    private lateinit var statData: TextView
    private lateinit var weekText: TextView
    private lateinit var chart: BarChart
    private var thumbs: ThumbnailLoader? = null
    private lateinit var pairTile: View

    /** When the current "Looking for your PC" started (elapsed ms), to add a hint if it takes long. */
    private var lookingSince = 0L
    private val slowCheck = Runnable { SyncEngine.current?.let { if (lookingSince != 0L) renderStatus(it) } }

    override fun build(): View {
        val root = page()
        root.addView(ui.headline("Midfielder").apply { setPadding(ui.dp(4), ui.dp(12), 0, ui.dp(16)) })

        // Hero: connection status on the pale-green container.
        val hero = ui.card(Ui.Card.FILLED, fill = ui.primaryContainer, paddingDp = 20)
        hero.card.cardElevation = 0f
        val heroTop = ui.horizontal()
        heroTop.addView(ui.iconBadge(R.drawable.ic_laptop, 52, bg = ui.primary, fg = ui.onPrimary))
        val names = ui.vertical().apply { setPadding(ui.dp(16), 0, 0, 0) }
        // Two lines: "No PC paired yet" or a long PC name must not be cut at large text sizes.
        pcName = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_TitleLarge, ui.onPrimaryContainer).apply {
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }
        names.addView(pcName)
        statusChip = Chip(host).apply {
            isClickable = false
            setOnClickListener { if (SyncEngine.current?.status?.value?.paired.isNullOrEmpty()) host.startScan() }
            chipStrokeWidth = 0f
            textSize = 12f
            chipMinHeight = ui.dpf(26f)
            setEnsureMinTouchTargetSize(false)
        }
        names.addView(statusChip, ui.wrap().apply { topMargin = ui.dp(4) })
        heroTop.addView(names, ui.weight())
        hero.body.addView(heroTop)
        capture = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_BodyMedium, ui.onPrimaryContainer).apply {
            alpha = 0.85f
            setPadding(0, ui.dp(14), 0, 0)
        }
        hero.body.addView(capture)
        pcBattery = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_BodyMedium, ui.onPrimaryContainer).apply {
            setPadding(0, ui.dp(8), 0, 0)
            setCompoundDrawablesRelativeWithIntrinsicBounds(R.drawable.ic_battery, 0, 0, 0)
            compoundDrawablePadding = ui.dp(6)
            compoundDrawableTintList = android.content.res.ColorStateList.valueOf(ui.onPrimaryContainer)
            visibility = View.GONE
        }
        hero.body.addView(pcBattery)
        pairButton = ui.button("Pair with your PC", Ui.Button.FILLED, R.drawable.ic_qr) { host.startScan() }
        hero.body.addView(pairButton, ui.matchWidth(16))
        root.addView(hero.card, ui.matchWidth(0))

        // Setup banner (only while something is missing).
        val setup = ui.card(Ui.Card.FILLED, fill = ui.tertiaryContainer, paddingDp = 16)
        setup.card.cardElevation = 0f
        setupCard = setup.card
        val setupRow = ui.horizontal()
        setupRow.addView(ui.icon(R.drawable.ic_bolt, ui.onTertiaryContainer, 22))
        setupText = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_LabelLarge, ui.onTertiaryContainer)
        setupRow.addView(setupText, ui.weight(1f, 12))
        setupRow.addView(ui.icon(R.drawable.ic_chevron_right, ui.onTertiaryContainer, 22))
        setup.body.addView(setupRow)
        setupCard.setOnClickListener { host.showSetup() }
        root.addView(setupCard, ui.matchWidth(12))

        // Quick actions: 2 x 2 tonal tiles.
        val grid = GridLayout(host).apply { columnCount = 2 }
        grid.addView(tile(R.drawable.ic_copy, "Clipboard", "Send it to your PC") { host.sendClipboardNow() }, cell(0))
        grid.addView(tile(R.drawable.ic_upload, "Send files", "Photos, docs, more") { host.pickFiles() }, cell(1))
        grid.addView(tile(R.drawable.ic_folder, "Received", "Open the folder") { FileActions.openFolder(host) }, cell(0))
        pairTile = tile(R.drawable.ic_qr, "Pair PC", "Scan a code") { host.startScan() }
        grid.addView(pairTile, cell(1))
        root.addView(grid, ui.matchWidth(12))

        // With the PC: one compact row, each tile only while its feature is on (on both devices). Kept out of the
        // hero so the top stays calm.
        pcActions = ui.horizontal().apply { visibility = View.GONE }
        ringTile = compactTile(R.drawable.ic_ring, "Ring PC") {
            val sent = SyncEngine.current?.features?.ringPc() == true
            host.snack(if (sent) "Ringing your PC" else "Your PC can't ring right now")
        }.first
        val pcAudio = compactTile(R.drawable.ic_headphones, "PC audio here") { togglePcAudio() }
        pcAudioTile = pcAudio.first
        pcAudioTitle = pcAudio.second
        phoneAudioTile = compactTile(R.drawable.ic_music, "Play on PC") {
            val sent = SyncEngine.current?.features?.requestAudio(dev.midfielder.proto.AudioRoute.AUDIO_ROUTE_PHONE_TO_PC, start = true) == true
            host.snack(if (sent) "Your PC is connecting to this phone over Bluetooth" else "Turn on Audio in Midfielder on your PC too")
        }.first
        for (t in listOf(ringTile, pcAudioTile, phoneAudioTile)) pcActions.addView(t, ui.weight().apply { setMargins(ui.dp(3), 0, ui.dp(3), 0) })
        root.addView(pcActions, ui.matchWidth(6))

        // Live transfers.
        val transfers = ui.card(Ui.Card.FILLED)
        transfersCard = transfers.card.apply { visibility = View.GONE }
        transfers.body.addView(ui.title("Transfers"))
        transfersList = ui.vertical()
        transfers.body.addView(transfersList)
        root.addView(transfersCard, ui.matchWidth(12))

        // Recent files strip.
        recentHeader = ui.sectionHeader("Recent files", "See all") { host.showTab(MainActivity.TAB_FILES) }
        root.addView(recentHeader)
        recentStrip = ui.horizontal().apply { setPadding(0, ui.dp(4), ui.dp(8), ui.dp(4)) }
        recentScroll = HorizontalScrollView(host).apply {
            isHorizontalScrollBarEnabled = false
            clipToPadding = false
            addView(recentStrip)
        }
        root.addView(recentScroll, ui.matchWidth(0))

        // Activity.
        root.addView(ui.sectionHeader("Activity"))
        val stats = ui.card(Ui.Card.FILLED, paddingDp = 20)
        val nums = ui.horizontal()
        statClips = stat(nums, "clips today")
        statFiles = stat(nums, "files today")
        statData = stat(nums, "moved today")
        stats.body.addView(nums)
        chart = BarChart(host, ui.secondaryContainer, ui.primary, ui.onSurfaceVariant)
        stats.body.addView(chart, ui.matchWidth(16))
        weekText = ui.small("").apply { setPadding(0, ui.dp(12), 0, 0) }
        stats.body.addView(weekText)
        root.addView(stats.card, ui.matchWidth(4))

        return ScrollView(host).apply {
            isFillViewport = true
            addView(root)
        }
    }

    private fun cell(col: Int) = GridLayout.LayoutParams(GridLayout.spec(GridLayout.UNDEFINED), GridLayout.spec(col, 1f)).apply {
        width = 0
        setMargins(if (col == 0) 0 else ui.dp(6), ui.dp(6), if (col == 0) ui.dp(6) else 0, ui.dp(6))
    }

    /** A small tile: icon above a one-line title. Returns the tile and its title (for live labels). */
    private fun compactTile(icon: Int, title: String, onClick: () -> Unit): Pair<View, TextView> {
        val box = ui.card(Ui.Card.FILLED, paddingDp = 12)
        box.body.gravity = android.view.Gravity.CENTER_HORIZONTAL
        box.body.addView(ui.iconBadge(icon, 36, ui.secondaryContainer, ui.onSecondaryContainer, cornerDp = 12))
        // Two lines, not one: with large text "Stop PC audio" must not become "Stop PC…".
        val label = ui.text(title, com.google.android.material.R.style.TextAppearance_Material3_LabelLarge, ui.onSurface).apply {
            maxLines = 2
            ellipsize = android.text.TextUtils.TruncateAt.END
            setPadding(0, ui.dp(8), 0, 0)
            gravity = android.view.Gravity.CENTER_HORIZONTAL
        }
        box.body.addView(label)
        box.card.isClickable = true
        box.card.contentDescription = title
        box.card.setOnClickListener { onClick() }
        return box.card to label
    }

    private fun tile(icon: Int, title: String, sub: String, onClick: () -> Unit): View {
        val box = ui.card(Ui.Card.FILLED, paddingDp = 16)
        box.body.addView(ui.iconBadge(icon, 44, ui.secondaryContainer, ui.onSecondaryContainer, cornerDp = 14))
        box.body.addView(ui.oneLine(ui.text(title, com.google.android.material.R.style.TextAppearance_Material3_TitleSmall, ui.onSurface)).apply {
            setPadding(0, ui.dp(12), 0, 0)
            tag = TITLE_TAG
        })
        box.body.addView(ui.oneLine(ui.small(sub)))
        box.card.isClickable = true
        box.card.contentDescription = "$title. $sub"
        box.card.setOnClickListener { onClick() }
        return box.card
    }

    private fun stat(parent: LinearLayout, label: String): TextView {
        val col = ui.vertical()
        val value = ui.text("0", com.google.android.material.R.style.TextAppearance_Material3_HeadlineSmall, ui.primary)
        col.addView(value)
        col.addView(ui.small(label))
        parent.addView(col, ui.weight())
        return value
    }

    override fun start(scope: CoroutineScope) {
        thumbs = ThumbnailLoader(host)
        refreshSetup()
        renderRecent()
        scope.launch {
            val engine = engine()
            launch { combine(engine.status, EventLog.entries) { s, _ -> s }.collect { renderStatus(engine) } }
            launch { engine.transfers.active.collect(::renderTransfers) }
            launch { engine.features.pc.collect(::renderPc) }
            launch {
                engine.history.version.collect {
                    renderStats(engine)
                    renderRecent()
                }
            }
        }
    }

    override fun stop() {
        thumbs?.close()
        thumbs = null
        shownRecent = null // thumbnails still loading were cut off with the loader: rebind on return
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
        val now = android.os.SystemClock.elapsedRealtime()
        if (connected || !paired) lookingSince = 0L else if (lookingSince == 0L) lookingSince = now
        val slow = lookingSince != 0L && now - lookingSince > SLOW_SEARCH_MS
        statusChip.text = when {
            connected -> "● Connected"
            paired && slow -> "Looking for your PC… Same Wi-Fi?"
            paired -> "Looking for your PC…"
            else -> "Scan the code on your PC"
        }
        // Read as words: TalkBack would say "black circle, Connected".
        statusChip.contentDescription = if (connected) "Connected" else statusChip.text
        statusChip.isClickable = !paired
        // Unpaired, the "Pair with your PC" button already says what to do; a chip saying it too only crowds the hero.
        statusChip.visibility = if (paired) View.VISIBLE else View.GONE
        statusChip.removeCallbacks(slowCheck)
        if (paired && !connected && !slow) statusChip.postDelayed(slowCheck, SLOW_SEARCH_MS + 500 - (now - lookingSince))
        (pairTile as? ViewGroup)?.findViewWithTag<TextView>(TITLE_TAG)?.text = if (paired) "Pair another PC" else "Pair PC"
        statusChip.chipBackgroundColor = android.content.res.ColorStateList.valueOf(if (connected) ui.primary else ui.containerLowest)
        statusChip.setTextColor(if (connected) ui.onPrimary else ui.onPrimaryContainer)
        pairButton.visibility = if (paired) View.GONE else View.VISIBLE
        capture.text = "Phone → PC copy: ${engine.captureMode}"
        capture.visibility = if (paired) View.VISIBLE else View.GONE
    }

    /** The PC's battery and the Ring PC button, each only while its feature is on (on both devices). */
    private fun renderPc(pc: dev.midfielder.android.PhoneFeatures.Pc) {
        val b = pc.battery
        pcBattery.visibility = if (b == null) View.GONE else View.VISIBLE
        if (b != null) pcBattery.text = "PC battery ${b.level}%" + when {
            b.charging -> " · charging"
            b.plugged -> " · plugged in"
            else -> ""
        }
        fun show(v: View, on: Boolean) {
            v.visibility = if (on) View.VISIBLE else View.GONE
        }
        val audioOn = storage.featAudio && pc.canAudio
        show(ringTile, storage.featRing && pc.canRing)
        show(pcAudioTile, audioOn && storage.audioPcToPhone)
        show(phoneAudioTile, audioOn && storage.audioPhoneToPc)
        show(pcActions, listOf(ringTile, pcAudioTile, phoneAudioTile).any { it.visibility == View.VISIBLE })
        pcAudioTitle.text = if (pc.audioHere) "Stop PC audio" else "PC audio here"
    }

    private fun togglePcAudio() {
        val features = SyncEngine.current?.features ?: return
        if (features.pc.value.audioHere) {
            features.stopPcAudio()
            return
        }
        val sent = features.requestAudio(dev.midfielder.proto.AudioRoute.AUDIO_ROUTE_PC_TO_PHONE, start = true)
        if (!sent) host.snack("Turn on Audio in Midfielder on your PC too")
    }

    private class TransferRow(val view: View, val detail: TextView, val bar: com.google.android.material.progressindicator.LinearProgressIndicator)

    private val transferRows = LinkedHashMap<String, TransferRow>()

    /**
     * Rows are kept per transfer and only their text and bar change: rebuilding them on every progress tick
     * restarted the bar's animation from 0 and could swallow a tap on Cancel.
     */
    private fun renderTransfers(list: List<ActiveTransfer>) {
        transfersCard.visibility = if (list.isEmpty()) View.GONE else View.VISIBLE
        val ids = list.map { it.id }.toSet()
        transferRows.keys.filter { it !in ids }.forEach { id -> transferRows.remove(id)?.let { transfersList.removeView(it.view) } }
        for (t in list) {
            val row = transferRows.getOrPut(t.id) { transferRow(t).also { transfersList.addView(it.view) } }
            val pct = if (t.percent >= 0) " · ${t.percent}%" else ""
            val verb = if (t.direction == Direction.SENT) "to" else "from"
            row.detail.text = "${Format.bytes(t.done)} of ${Format.bytes(t.total)} $verb ${t.peer}$pct"
            row.bar.isIndeterminate = t.percent < 0
            if (t.percent >= 0) row.bar.setProgressCompat(t.percent, true)
        }
    }

    private fun transferRow(t: ActiveTransfer): TransferRow {
        val box = ui.vertical().apply { setPadding(0, ui.dp(12), 0, 0) }
        val top = ui.horizontal()
        top.addView(ui.iconBadge(if (t.direction == Direction.SENT) R.drawable.ic_upload else R.drawable.ic_download, 40))
        val texts = ui.vertical().apply { setPadding(ui.dp(12), 0, 0, 0) }
        texts.addView(ui.oneLine(ui.title(t.title), TextUtils.TruncateAt.MIDDLE))
        val detail = ui.oneLine(ui.small(""))
        texts.addView(detail)
        top.addView(texts, ui.weight())
        top.addView(ui.iconButton(R.drawable.ic_close, "Cancel ${t.title}") { SyncEngine.current?.cancelTransfer(t.id) })
        box.addView(top)
        val bar = ui.progress()
        box.addView(bar, ui.matchWidth(10))
        return TransferRow(box, detail, bar)
    }

    private var shownRecent: List<dev.midfielder.core.history.TransferRecord>? = null

    private fun renderRecent() {
        if (!::recentStrip.isInitialized) return
        val recent = HistoryStore.get(host).records().take(10)
        // History changes on every clipboard sync too: rebuild the strip only when its files changed.
        if (recent == shownRecent) return
        shownRecent = recent
        recentHeader.visibility = if (recent.isEmpty()) View.GONE else View.VISIBLE
        recentScroll.visibility = recentHeader.visibility
        recentStrip.removeAllViews()
        val size = ui.dp(104)
        for (r in recent) {
            val item = ui.vertical().apply {
                isClickable = true
                background = ui.ripple()
                setOnClickListener { FileActions.open(host, r) }
                contentDescription = "${r.name}, ${if (r.direction == Direction.SENT) "sent to" else "from"} ${r.peer}" +
                    if (r.status != dev.midfielder.core.history.RecordStatus.DONE) ", not completed" else ""
            }
            val frame = FrameLayout(host)
            val image = ShapeableImageView(host).apply {
                shapeAppearanceModel = shapeAppearanceModel.toBuilder().setAllCornerSizes(ui.dpf(16f)).build()
            }
            frame.addView(image, FrameLayout.LayoutParams(size, size))
            Thumbs.bind(ui, image, r, thumbs, size)
            frame.addView(
                ui.iconBadge(if (r.direction == Direction.SENT) R.drawable.ic_upload else R.drawable.ic_download, 22, ui.containerLowest, ui.primary),
                FrameLayout.LayoutParams(ui.dp(22), ui.dp(22), Gravity.TOP or Gravity.END).apply { setMargins(0, ui.dp(6), ui.dp(6), 0) },
            )
            item.addView(frame)
            item.addView(ui.oneLine(ui.small(r.name), TextUtils.TruncateAt.MIDDLE).apply {
                gravity = Gravity.CENTER
                setPadding(ui.dp(2), ui.dp(6), ui.dp(2), 0)
            }, LinearLayout.LayoutParams(size, ViewGroup.LayoutParams.WRAP_CONTENT))
            recentStrip.addView(item, ui.wrap().apply { marginEnd = ui.dp(10) })
        }
    }

    private fun renderStats(engine: SyncEngine) {
        val days = engine.history.stats.lastDays(7, System.currentTimeMillis())
        val today = days.last()
        statClips.text = today.clips.toString()
        statFiles.text = today.files.toString()
        statData.text = Format.bytes(today.bytes)
        val labels = (6 downTo 0).map {
            LocalDate.now().minusDays(it.toLong()).dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault())
        }
        chart.set(days.map { it.clips + it.files }, labels)
        val week = days.reduce { a, b -> a + b }
        weekText.text = "Last 7 days: ${week.clips} clips · ${week.files} files · ${Format.bytes(week.bytes)}"
    }

    private companion object {
        const val SLOW_SEARCH_MS = 30_000L
        const val TITLE_TAG = "tile-title"
    }
}
