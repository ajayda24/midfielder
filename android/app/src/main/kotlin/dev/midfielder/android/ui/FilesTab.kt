package dev.midfielder.android.ui

import android.content.Context
import android.content.res.ColorStateList
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.AbsListView
import android.widget.BaseAdapter
import android.widget.FrameLayout
import android.widget.GridView
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.PopupMenu
import android.widget.TextView
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

/** History of sent and received files as a list or a gallery grid. Tap opens, the ⋮ menu shares or deletes. */
class FilesTab(host: MainActivity) : Tab(host) {
    private enum class Filter(val label: String) { ALL("All"), RECEIVED("Received"), SENT("Sent"), IMAGES("Images") }

    private val store = HistoryStore.get(host)
    private var thumbs: ThumbnailLoader? = null
    private var items: List<TransferRecord> = emptyList()
    private lateinit var chips: LinearLayout
    private lateinit var content: FrameLayout
    private lateinit var empty: View
    private lateinit var toggle: ImageView
    private var listView: AbsListView? = null

    private val adapter = object : BaseAdapter() {
        override fun getCount() = items.size
        override fun getItem(position: Int) = items[position]
        override fun getItemId(position: Int) = items[position].id.hashCode().toLong()
        override fun getView(position: Int, convertView: View?, parent: ViewGroup): View =
            if (gallery) tile(items[position], convertView as? TileView) else row(items[position], convertView as? RowView)
    }

    override fun build(): View {
        val root = ui.vertical().apply { setPadding(ui.dp(16), ui.dp(8), ui.dp(16), 0) }
        val header = ui.horizontal()
        header.addView(ui.title("Files"), ui.weight())
        header.addView(ui.smallButton("Open folder") { FileActions.openFolder(host) })
        toggle = ui.iconButton(R.drawable.ic_grid, "Gallery view", Palette.PRIMARY) { setGallery(!gallery) }
        header.addView(toggle)
        header.addView(ui.iconButton(R.drawable.ic_more, "More", Palette.MUTED) {}.apply { setOnClickListener { headerMenu(it) } })
        root.addView(header)

        chips = ui.horizontal()
        root.addView(
            HorizontalScrollView(host).apply {
                isHorizontalScrollBarEnabled = false
                addView(chips)
            },
            ui.matchWidth(4),
        )

        content = FrameLayout(host)
        empty = ui.vertical().apply {
            gravity = Gravity.CENTER
            addView(ui.iconBadge(R.drawable.ic_folder, 64))
            addView(ui.text("No files yet", 17f, bold = true).apply { setPadding(0, ui.dp(16), 0, ui.dp(4)) })
            addView(ui.text("Files you send or receive show up here.\nShare anything to \"Send to PC\" to start.", 14f, Palette.MUTED).apply {
                gravity = Gravity.CENTER
            })
        }
        content.addView(empty, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = ui.dp(8) })
        renderChips()
        setGallery(gallery)
        return root
    }

    override fun start(scope: CoroutineScope) {
        thumbs = ThumbnailLoader(host)
        adapter.notifyDataSetChanged()
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

    private fun reload() {
        val all = store.records()
        items = when (filter) {
            Filter.ALL -> all
            Filter.RECEIVED -> all.filter { it.direction == Direction.RECEIVED }
            Filter.SENT -> all.filter { it.direction == Direction.SENT }
            Filter.IMAGES -> all.filter { it.isImage }
        }
        empty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
        listView?.visibility = if (items.isEmpty()) View.GONE else View.VISIBLE
        adapter.notifyDataSetChanged()
    }

    private fun renderChips() {
        chips.removeAllViews()
        for (f in Filter.entries) {
            chips.addView(
                ui.chip(f.label, f == filter) {
                    filter = f
                    renderChips()
                    reload()
                },
                LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginEnd = ui.dp(8) },
            )
        }
    }

    private fun setGallery(on: Boolean) {
        gallery = on
        toggle.setImageResource(if (on) R.drawable.ic_list else R.drawable.ic_grid)
        toggle.contentDescription = if (on) "List view" else "Gallery view"
        listView?.let { content.removeView(it) }
        val view: AbsListView = if (on) {
            GridView(host).apply {
                numColumns = 3
                stretchMode = GridView.STRETCH_COLUMN_WIDTH
                horizontalSpacing = ui.dp(6)
                verticalSpacing = ui.dp(6)
            }
        } else {
            ListView(host).apply {
                divider = null
                dividerHeight = 0
            }
        }
        view.apply {
            clipToPadding = false
            setPadding(0, 0, 0, ui.dp(16))
            selector = ui.rounded(Palette.HIGHLIGHT, 12)
            adapter = this@FilesTab.adapter
            setOnItemClickListener { _, _, pos, _ -> FileActions.open(host, items[pos]) }
            setOnItemLongClickListener { _, v, pos, _ ->
                itemMenu(v, items[pos])
                true
            }
        }
        content.addView(view, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        listView = view
        reload()
    }

    private fun headerMenu(anchor: View) {
        PopupMenu(host, anchor).apply {
            menu.add("Clear history").setOnMenuItemClickListener {
                host.confirm("Clear the list?", "Files stay on your phone; only the history is cleared.", "Clear") { store.clearHistory() }
                true
            }
            show()
        }
    }

    private fun itemMenu(anchor: View, r: TransferRecord) {
        val onPhone = FileActions.uriOf(r) != null
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
            menu.add("Remove from list").setOnMenuItemClickListener { store.remove(listOf(r.id)); true }
            show()
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Rows and tiles

    private inner class RowView(context: Context) : LinearLayout(context) {
        val thumb = ImageView(context)
        val name: TextView = ui.text("", 15f, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.MIDDLE }
        val meta: TextView = ui.text("", 12f, Palette.MUTED).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END }
        val more: ImageView = ui.iconButton(R.drawable.ic_more, "More", Palette.MUTED) {}
        var record: TransferRecord? = null

        init {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(ui.dp(4), ui.dp(8), 0, ui.dp(8))
            thumb.scaleType = ImageView.ScaleType.CENTER_CROP
            thumb.clipToOutline = true
            addView(thumb, LayoutParams(ui.dp(52), ui.dp(52)))
            val texts = ui.vertical().apply { setPadding(ui.dp(14), 0, ui.dp(4), 0) }
            texts.addView(name)
            texts.addView(meta)
            addView(texts, LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(more)
            more.isFocusable = false
            more.setOnClickListener { record?.let { itemMenu(more, it) } }
        }
    }

    private fun row(r: TransferRecord, recycled: RowView?): View {
        val v = recycled ?: RowView(host)
        v.record = r
        v.name.text = r.name
        val dir = if (r.direction == Direction.SENT) "↑ to ${r.peer}" else "↓ from ${r.peer}"
        val state = when (r.status) {
            RecordStatus.DONE -> ""
            RecordStatus.FAILED -> " · failed"
            RecordStatus.CANCELLED -> " · cancelled"
        }
        v.meta.text = "$dir · ${Format.bytes(r.size)} · ${Format.time(r.timeMs)}$state"
        v.meta.setTextColor(if (r.status == RecordStatus.DONE) Palette.MUTED else Palette.DANGER)
        bindThumb(v.thumb, r, ui.dp(52))
        return v
    }

    private inner class TileView(context: Context) : FrameLayout(context) {
        val thumb = ImageView(context)
        val label: TextView = ui.text("", 11f, Palette.MUTED).apply {
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.MIDDLE
            gravity = Gravity.CENTER
            setPadding(ui.dp(6), 0, ui.dp(6), ui.dp(6))
        }

        init {
            thumb.scaleType = ImageView.ScaleType.CENTER_CROP
            addView(thumb, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            addView(label, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
            background = ui.rounded(Palette.HIGHLIGHT, 12)
            clipToOutline = true
        }

        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) = super.onMeasure(widthMeasureSpec, widthMeasureSpec)
    }

    private fun tile(r: TransferRecord, recycled: TileView?): View {
        val v = recycled ?: TileView(host)
        v.label.text = if (r.isImage) "" else r.name
        v.label.visibility = if (r.isImage) View.GONE else View.VISIBLE
        bindThumb(v.thumb, r, ui.dp(120))
        return v
    }

    private fun bindThumb(view: ImageView, r: TransferRecord, sizePx: Int) {
        view.background = ui.rounded(Palette.HIGHLIGHT, 12)
        val fallback = {
            view.setImageResource(R.drawable.ic_file)
            view.imageTintList = ColorStateList.valueOf(Palette.PRIMARY)
            view.scaleType = ImageView.ScaleType.CENTER
        }
        val uri = FileActions.uriOf(r)
        val loader = thumbs
        val hasThumb = r.isImage || r.mime.startsWith("video/") || r.mime == "application/pdf"
        if (uri == null || loader == null || !hasThumb) {
            view.tag = null
            fallback()
            return
        }
        loader.load(uri, sizePx, view, fallback) { view.scaleType = ImageView.ScaleType.CENTER_CROP }
    }

    private companion object {
        // Remembered while the app process lives, so switching tabs keeps your view.
        var gallery = false
        var filter = Filter.ALL
    }
}
