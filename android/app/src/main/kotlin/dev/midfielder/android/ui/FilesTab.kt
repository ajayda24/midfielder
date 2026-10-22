package dev.midfielder.android.ui

import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.TextView
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.button.MaterialButton
import com.google.android.material.chip.ChipGroup
import com.google.android.material.imageview.ShapeableImageView
import dev.midfielder.android.FileActions
import dev.midfielder.android.HistoryStore
import dev.midfielder.android.MainActivity
import dev.midfielder.android.R
import dev.midfielder.core.history.Direction
import dev.midfielder.core.history.RecordStatus
import dev.midfielder.core.history.TransferRecord
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.Calendar

/**
 * Sent and received files, grouped by day, as a list or a gallery grid. Tap opens; long-press starts
 * multi-select (share / delete); the ⋮ menu has the rest.
 */
class FilesTab(host: MainActivity) : Tab(host) {
    private enum class Filter(val label: String) { ALL("All"), RECEIVED("Received"), SENT("Sent"), IMAGES("Images") }

    private sealed class Item {
        data class Header(val title: String) : Item()
        data class Row(val record: TransferRecord) : Item()
    }

    private val store = HistoryStore.get(host)
    private var thumbs: ThumbnailLoader? = null
    private var items: List<Item> = emptyList()
    private val selected = LinkedHashSet<String>()
    private lateinit var list: RecyclerView
    private lateinit var grid: GridLayoutManager
    private lateinit var empty: View
    private lateinit var toggle: MaterialButton
    private lateinit var titleBar: View
    private lateinit var selectBar: LinearLayout
    private lateinit var selectCount: TextView
    private lateinit var emptyTitle: TextView
    private lateinit var emptyText: TextView
    private lateinit var emptyShowAll: View
    private lateinit var chipIds: Map<Filter, Int>
    private lateinit var chipGroup: ChipGroup

    /** Tiles about 120 dp wide: 3 on a phone, more on a tablet or in landscape. */
    private val columns: Int get() = (host.resources.configuration.screenWidthDp / 120).coerceIn(3, 6)

    override fun build(): View {
        val root = ui.vertical()

        // Title bar, and the selection bar that replaces it in multi-select.
        titleBar = ui.horizontal().apply {
            setPadding(ui.dp(20), ui.dp(12), ui.dp(8), ui.dp(4))
            addView(ui.headline("Files"), ui.weight())
            toggle = ui.iconButton(R.drawable.ic_grid, "Gallery view", ui.primary) { setGallery(!gallery) }
            addView(toggle)
            addView(ui.iconButton(R.drawable.ic_more, "More") { headerMenu(it) })
        }
        root.addView(titleBar)
        selectBar = ui.horizontal().apply {
            setPadding(ui.dp(8), ui.dp(8), ui.dp(8), ui.dp(8))
            background = ui.pill(ui.secondaryContainer, 0)
            visibility = View.GONE
            addView(ui.iconButton(R.drawable.ic_close, "Cancel selection", ui.onSecondaryContainer) { clearSelection() })
            selectCount = ui.text("", com.google.android.material.R.style.TextAppearance_Material3_TitleMedium, ui.onSecondaryContainer)
            addView(selectCount, ui.weight(1f, 4))
            addView(ui.iconButton(R.drawable.ic_share, "Share", ui.onSecondaryContainer) {
                val records = selectedRecords()
                val ok = records.filter { r -> FileActions.readable(host, r) }
                if (ok.isEmpty()) {
                    host.snack(if (records.size == 1) "This file is no longer on the phone" else "None of these files are on the phone anymore")
                } else {
                    if (ok.size < records.size) host.snack("${records.size - ok.size} of ${records.size} aren't on the phone anymore; sharing the rest")
                    FileActions.share(host, ok)
                    clearSelection()
                }
            })
            addView(ui.iconButton(R.drawable.ic_delete, "Delete", ui.onSecondaryContainer) { deleteSelected() })
        }
        root.addView(selectBar, ui.matchWidth(0))

        val chips = ChipGroup(host).apply {
            isSingleSelection = true
            isSelectionRequired = true
            isSingleLine = true
        }
        val ids = HashMap<Int, Filter>()
        for (f in Filter.entries) {
            val chip = ui.filterChip(f.label, f == filter)
            if (chip.id == View.NO_ID) chip.id = View.generateViewId()
            ids[chip.id] = f
            chips.addView(chip)
        }
        chipIds = ids.entries.associate { (id, f) -> f to id }
        chipGroup = chips
        chips.setOnCheckedStateChangeListener { _, checked ->
            checked.firstOrNull()?.let { ids[it] }?.let {
                if (it == filter) return@let
                filter = it
                reload()
                list.scrollToPosition(0) // a new filter starts at the top, not mid-way down the old list
            }
        }
        root.addView(
            android.widget.HorizontalScrollView(host).apply {
                isHorizontalScrollBarEnabled = false
                setPadding(ui.dp(16), 0, ui.dp(16), 0)
                clipToPadding = false
                addView(chips)
            },
            ui.matchWidth(0),
        )

        val content = FrameLayout(host)
        grid = GridLayoutManager(host, if (gallery) columns else 1)
        grid.spanSizeLookup = object : GridLayoutManager.SpanSizeLookup() {
            override fun getSpanSize(position: Int) = if (items.getOrNull(position) is Item.Header) grid.spanCount else 1
        }
        list = RecyclerView(host).apply {
            layoutManager = grid
            adapter = this@FilesTab.adapter
            clipToPadding = false
            setPadding(ui.dp(12), ui.dp(4), ui.dp(12), ui.dp(24))
            setHasFixedSize(true)
        }
        content.addView(list, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        empty = ui.vertical().apply {
            gravity = Gravity.CENTER
            setPadding(ui.dp(32), 0, ui.dp(32), ui.dp(48))
            addView(ui.iconBadge(R.drawable.ic_folder, 88, ui.secondaryContainer, ui.onSecondaryContainer))
            emptyTitle = ui.titleLarge("No files yet").apply {
                setPadding(0, ui.dp(20), 0, ui.dp(6))
                gravity = Gravity.CENTER
            }
            addView(emptyTitle)
            emptyText = ui.supporting("").apply { gravity = Gravity.CENTER }
            addView(emptyText)
            emptyShowAll = ui.button("Show all files", Ui.Button.TONAL) { chipIds[Filter.ALL]?.let { chipGroup.check(it) } }
            addView(emptyShowAll, ui.wrap().apply { topMargin = ui.dp(16) })
        }
        content.addView(empty, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        updateToggle()
        return root
    }

    override fun start(scope: CoroutineScope) {
        thumbs = ThumbnailLoader(host)
        scope.launch { store.version.collect { reload() } }
        // Drop entries for files deleted in other apps (one batched query, off the main thread).
        scope.launch {
            val gone = withContext(Dispatchers.IO) {
                runCatching { FileActions.missing(host, store.records().filter { it.direction == Direction.RECEIVED }) }.getOrDefault(emptyList())
            }
            if (gone.isNotEmpty()) store.remove(gone)
        }
    }

    override fun stop() {
        thumbs?.close()
        thumbs = null
    }

    override fun onBack(): Boolean {
        if (selected.isEmpty()) return false
        clearSelection()
        return true
    }

    // ---------------------------------------------------------------------------------------------

    private fun reload() {
        val all = store.records()
        val records = when (filter) {
            Filter.ALL -> all
            Filter.RECEIVED -> all.filter { it.direction == Direction.RECEIVED }
            Filter.SENT -> all.filter { it.direction == Direction.SENT }
            Filter.IMAGES -> all.filter { it.isImage }
        }
        selected.retainAll(records.map { it.id }.toSet())
        val old = items
        val new = grouped(records)
        // Only what changed moves: a new file slides in, the rest keep their place and their thumbnails.
        val diff = DiffUtil.calculateDiff(object : DiffUtil.Callback() {
            override fun getOldListSize() = old.size
            override fun getNewListSize() = new.size
            override fun areItemsTheSame(o: Int, n: Int) = key(old[o]) == key(new[n])
            override fun areContentsTheSame(o: Int, n: Int) = old[o] == new[n]
        })
        items = new
        diff.dispatchUpdatesTo(adapter)
        renderEmpty(all.isEmpty(), records.isEmpty())
        updateSelectionBar()
    }

    private fun key(i: Item): String = when (i) {
        is Item.Header -> "h:" + i.title
        is Item.Row -> i.record.id
    }

    private fun renderEmpty(nothingAtAll: Boolean, nothingHere: Boolean) {
        empty.visibility = if (nothingHere) View.VISIBLE else View.GONE
        list.visibility = if (nothingHere) View.GONE else View.VISIBLE
        if (!nothingHere) return
        emptyTitle.text = when {
            nothingAtAll -> "No files yet"
            filter == Filter.RECEIVED -> "Nothing received yet"
            filter == Filter.SENT -> "Nothing sent yet"
            else -> "No images yet"
        }
        emptyText.text = when {
            nothingAtAll -> "Files you send or receive show up here. Share anything and pick your PC to start."
            filter == Filter.RECEIVED -> "Files your PC sends to this phone show up here."
            filter == Filter.SENT -> "Share a file and pick your PC, or tap Send files on Home."
            else -> "Photos and screenshots you send or receive show up here."
        }
        emptyShowAll.visibility = if (nothingAtAll) View.GONE else View.VISIBLE
    }

    private fun grouped(records: List<TransferRecord>): List<Item> {
        val cal = Calendar.getInstance()
        cal.set(Calendar.HOUR_OF_DAY, 0); cal.set(Calendar.MINUTE, 0); cal.set(Calendar.SECOND, 0); cal.set(Calendar.MILLISECOND, 0)
        val today = cal.timeInMillis
        val yesterday = today - 86_400_000L
        val week = today - 6 * 86_400_000L
        val out = ArrayList<Item>(records.size + 4)
        var last: String? = null
        for (r in records) {
            val section = when {
                r.timeMs >= today -> "Today"
                r.timeMs >= yesterday -> "Yesterday"
                r.timeMs >= week -> "This week"
                else -> "Earlier"
            }
            if (section != last) {
                out += Item.Header(section)
                last = section
            }
            out += Item.Row(r)
        }
        return out
    }

    private fun setGallery(on: Boolean) {
        gallery = on
        grid.spanCount = if (on) columns else 1
        updateToggle()
        adapter.notifyDataSetChanged()
    }

    private fun updateToggle() {
        toggle.setIconResource(if (gallery) R.drawable.ic_list else R.drawable.ic_grid)
        toggle.contentDescription = if (gallery) "List view" else "Gallery view"
    }

    private fun selectedRecords(): List<TransferRecord> =
        items.mapNotNull { (it as? Item.Row)?.record }.filter { it.id in selected }

    private fun toggleSelection(r: TransferRecord) {
        val wasEmpty = selected.isEmpty()
        if (!selected.remove(r.id)) selected += r.id
        // Entering or leaving selection changes every row's ⋮ button; otherwise only this one row changes.
        if (wasEmpty != selected.isEmpty()) selectionChanged()
        else items.indexOfFirst { it is Item.Row && it.record.id == r.id }.takeIf { it >= 0 }?.let { adapter.notifyItemChanged(it, SELECTION) }
        updateSelectionBar()
    }

    private fun clearSelection() {
        selected.clear()
        selectionChanged()
        updateSelectionBar()
    }

    /** Rebinds only the selection look (no thumbnail reload, no flicker). */
    private fun selectionChanged() = adapter.notifyItemRangeChanged(0, items.size, SELECTION)

    private fun updateSelectionBar() {
        val on = selected.isNotEmpty()
        selectBar.visibility = if (on) View.VISIBLE else View.GONE
        titleBar.visibility = if (on) View.GONE else View.VISIBLE
        selectCount.text = "${selected.size} selected"
        selectCount.announceForAccessibility(selectCount.text)
    }

    private fun deleteSelected() {
        val records = selectedRecords()
        if (records.isEmpty()) return
        val onPhone = records.filter { it.direction == Direction.RECEIVED && FileActions.uriOf(it) != null }
        val others = records - onPhone.toSet()
        val what = if (records.size == 1) "\"${records[0].name}\"" else "${records.size} files"
        val one = records.size == 1
        host.confirm(
            "Delete $what?",
            when {
                onPhone.isNotEmpty() && others.isEmpty() -> if (one) "It's removed from Download/Midfielder." else "They're removed from Download/Midfielder."
                onPhone.isNotEmpty() -> "Received files are removed from Download/Midfielder. Sent files only leave this list."
                one -> "It's removed from this list. The file itself stays where it is."
                else -> "They're removed from this list. The files themselves stay where they are."
            },
            "Delete",
        ) {
            if (onPhone.isNotEmpty()) host.deleteReceived(onPhone)
            if (others.isNotEmpty()) store.remove(others.map { it.id })
            clearSelection()
        }
    }

    private fun removeWithUndo(records: List<TransferRecord>) {
        store.remove(records.map { it.id })
        host.snack(if (records.size == 1) "Removed from list" else "${records.size} removed from list", "Undo") { store.restore(records) }
    }

    private fun headerMenu(anchor: View) {
        PopupMenu(host, anchor).apply {
            menu.add("Open folder").setOnMenuItemClickListener { FileActions.openFolder(host); true }
            menu.add("Select all").setOnMenuItemClickListener {
                items.forEach { if (it is Item.Row) selected += it.record.id }
                selectionChanged()
                updateSelectionBar()
                true
            }
            menu.add("Clear history").setOnMenuItemClickListener {
                host.confirm("Clear the list?", "Files stay on your phone; only the history is cleared.", "Clear") { store.clearHistory() }
                true
            }
            show()
        }
    }

    private fun itemMenu(anchor: View, r: TransferRecord) {
        val onPhone = FileActions.readable(host, r)
        PopupMenu(host, anchor).apply {
            if (onPhone) {
                menu.add("Open").setOnMenuItemClickListener { FileActions.open(host, r); true }
                menu.add("Share").setOnMenuItemClickListener { FileActions.share(host, listOf(r)); true }
            }
            if (r.direction == Direction.RECEIVED && onPhone) {
                menu.add("Show in folder").setOnMenuItemClickListener { FileActions.openFolder(host); true }
                menu.add("Delete from phone").setOnMenuItemClickListener {
                    host.confirm("Delete ${r.name}?", "The file is removed from Download/Midfielder.", "Delete") { host.deleteReceived(listOf(r)) }
                    true
                }
            }
            menu.add("Remove from list").setOnMenuItemClickListener { removeWithUndo(listOf(r)); true }
            menu.add("Select").setOnMenuItemClickListener { toggleSelection(r); true }
            show()
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Adapter

    private class Holder(view: View) : RecyclerView.ViewHolder(view)

    private val adapter = object : RecyclerView.Adapter<Holder>() {
        override fun getItemCount() = items.size

        override fun getItemViewType(position: Int) = when {
            items[position] is Item.Header -> 0
            gallery -> 2
            else -> 1
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder = Holder(
            when (viewType) {
                0 -> ui.text("", com.google.android.material.R.style.TextAppearance_Material3_TitleSmall, ui.primary).apply {
                    setPadding(ui.dp(8), ui.dp(16), ui.dp(8), ui.dp(8))
                    layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
                }
                2 -> TileView()
                else -> RowView()
            },
        )

        override fun onBindViewHolder(holder: Holder, position: Int, payloads: MutableList<Any>) {
            if (payloads.isNotEmpty() && payloads.all { it == SELECTION }) {
                when (val v = holder.itemView) {
                    is RowView -> v.bindSelection()
                    is TileView -> v.bindSelection()
                }
                return
            }
            onBindViewHolder(holder, position)
        }

        override fun onBindViewHolder(holder: Holder, position: Int) {
            when (val item = items[position]) {
                is Item.Header -> (holder.itemView as TextView).text = item.title
                is Item.Row -> when (val v = holder.itemView) {
                    is RowView -> v.bind(item.record)
                    is TileView -> v.bind(item.record)
                }
            }
        }
    }

    private fun click(r: TransferRecord) {
        if (selected.isNotEmpty()) toggleSelection(r) else FileActions.open(host, r)
    }

    private inner class RowView : LinearLayout(host) {
        private val thumb = ShapeableImageView(host).apply {
            shapeAppearanceModel = shapeAppearanceModel.toBuilder().setAllCornerSizes(ui.dpf(12f)).build()
        }
        private val badge = ui.iconBadge(R.drawable.ic_download, 20, ui.containerLowest, ui.primary)
        private val check = ui.iconBadge(R.drawable.ic_check, 24, ui.primary, ui.onPrimary)
        private val name: TextView = ui.oneLine(ui.text("", com.google.android.material.R.style.TextAppearance_Material3_BodyLarge, ui.onSurface), TextUtils.TruncateAt.MIDDLE)
        private val meta: TextView = ui.oneLine(ui.small(""))
        private val more = ui.iconButton(R.drawable.ic_more, "More") { v -> record?.let { itemMenu(v, it) } }
        private var record: TransferRecord? = null

        init {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = ui.dp(76)
            setPadding(ui.dp(8), ui.dp(8), ui.dp(4), ui.dp(8))
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = ui.dp(2)
            }
            val frame = FrameLayout(host)
            frame.addView(thumb, FrameLayout.LayoutParams(ui.dp(56), ui.dp(56)))
            frame.addView(badge, FrameLayout.LayoutParams(ui.dp(20), ui.dp(20), Gravity.BOTTOM or Gravity.END))
            frame.addView(check, FrameLayout.LayoutParams(ui.dp(24), ui.dp(24), Gravity.CENTER))
            addView(frame, LayoutParams(ui.dp(60), ui.dp(60)))
            val texts = ui.vertical().apply { setPadding(ui.dp(14), 0, ui.dp(4), 0) }
            texts.addView(name)
            texts.addView(meta)
            addView(texts, LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(more)
            isClickable = true
            setOnClickListener { record?.let(::click) }
            setOnLongClickListener {
                record?.let(::toggleSelection)
                true
            }
        }

        fun bind(r: TransferRecord) {
            record = r
            name.text = r.name
            val dir = if (r.direction == Direction.SENT) "To ${r.peer}" else "From ${r.peer}"
            // A problem comes first, where it's seen even when the line is cut short.
            val state = when (r.status) {
                RecordStatus.DONE -> ""
                RecordStatus.FAILED -> if (r.direction == Direction.SENT) "Not sent · " else "Failed · "
                RecordStatus.CANCELLED -> "Cancelled · "
            }
            meta.text = "$state${Format.bytes(r.size)} · $dir · ${Format.time(r.timeMs)}"
            contentDescription = "${r.name}. ${meta.text}"
            meta.setTextColor(if (r.status == RecordStatus.DONE) ui.onSurfaceVariant else ui.errorColor)
            (badge as? FrameLayout)?.getChildAt(0)?.let {
                (it as android.widget.ImageView).setImageResource(if (r.direction == Direction.SENT) R.drawable.ic_upload else R.drawable.ic_download)
            }
            bindSelection()
            Thumbs.bind(ui, thumb, r, thumbs, ui.dp(56))
        }

        fun bindSelection() {
            val isSelected = record?.id in selected
            check.visibility = if (isSelected) View.VISIBLE else View.GONE
            background = if (isSelected) ui.pill(ui.secondaryContainer, 16) else ui.ripple()
            more.visibility = if (selected.isEmpty()) View.VISIBLE else View.INVISIBLE
            this.isSelected = isSelected
        }
    }

    private inner class TileView : FrameLayout(host) {
        private val thumb = ShapeableImageView(host).apply {
            shapeAppearanceModel = shapeAppearanceModel.toBuilder().setAllCornerSizes(ui.dpf(14f)).build()
        }
        private val label: TextView = ui.small("").apply {
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.MIDDLE
            gravity = Gravity.CENTER
            setPadding(ui.dp(8), 0, ui.dp(8), ui.dp(10))
        }
        private val check = ui.iconBadge(R.drawable.ic_check, 26, ui.primary, ui.onPrimary)
        private val video = ui.iconBadge(R.drawable.ic_play, 32, 0x99000000.toInt(), 0xFFFFFFFF.toInt())
        private val failed = ui.iconBadge(R.drawable.ic_close, 22, ui.errorColor, ui.color(com.google.android.material.R.attr.colorOnError))
        private var record: TransferRecord? = null

        init {
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                setMargins(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4))
            }
            addView(thumb, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            addView(label, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
            addView(video, LayoutParams(ui.dp(32), ui.dp(32), Gravity.CENTER))
            addView(check, LayoutParams(ui.dp(26), ui.dp(26), Gravity.TOP or Gravity.END).apply { setMargins(0, ui.dp(8), ui.dp(8), 0) })
            addView(failed, LayoutParams(ui.dp(22), ui.dp(22), Gravity.TOP or Gravity.START).apply { setMargins(ui.dp(8), ui.dp(8), 0, 0) })
            isClickable = true
            foreground = ui.ripple()
            setOnClickListener { record?.let(::click) }
            setOnLongClickListener {
                record?.let(::toggleSelection)
                true
            }
        }

        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) = super.onMeasure(widthMeasureSpec, widthMeasureSpec)

        fun bind(r: TransferRecord) {
            record = r
            val isMedia = r.isImage || r.mime.startsWith("video/")
            label.text = if (isMedia) "" else r.name
            label.visibility = if (isMedia) View.GONE else View.VISIBLE
            video.visibility = if (r.mime.startsWith("video/")) View.VISIBLE else View.GONE
            failed.visibility = if (r.status == RecordStatus.DONE) View.GONE else View.VISIBLE
            val state = when (r.status) {
                RecordStatus.DONE -> ""
                RecordStatus.FAILED -> if (r.direction == Direction.SENT) ", not sent" else ", failed"
                RecordStatus.CANCELLED -> ", cancelled"
            }
            contentDescription = "${r.name}$state, ${if (r.direction == Direction.SENT) "to" else "from"} ${r.peer}, ${Format.time(r.timeMs)}"
            bindSelection()
            Thumbs.bind(ui, thumb, r, thumbs, ui.dp(140))
        }

        fun bindSelection() {
            val isSelected = record?.id in selected
            check.visibility = if (isSelected) View.VISIBLE else View.GONE
            thumb.alpha = if (isSelected) 0.7f else 1f
            this.isSelected = isSelected
        }
    }

    private companion object {
        // Remembered while the app process lives, so switching tabs keeps your view.
        var gallery = false
        var filter = Filter.ALL
        val SELECTION = Any()
    }
}
