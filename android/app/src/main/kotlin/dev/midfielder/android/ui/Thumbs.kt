package dev.midfielder.android.ui

import android.content.res.ColorStateList
import android.widget.ImageView
import dev.midfielder.android.FileActions
import dev.midfielder.android.R
import dev.midfielder.core.history.TransferRecord

/** Shared thumbnail binding: real thumbnail for images/videos/PDFs, otherwise a type icon on a pale tile. */
object Thumbs {
    fun iconFor(r: TransferRecord): Int = when {
        r.isImage -> R.drawable.ic_image
        r.mime.startsWith("video/") -> R.drawable.ic_play
        else -> R.drawable.ic_file
    }

    fun bind(ui: Ui, view: ImageView, r: TransferRecord, loader: ThumbnailLoader?, sizePx: Int) {
        view.setBackgroundColor(ui.secondaryContainer)
        val fallback = {
            view.setImageResource(iconFor(r))
            view.imageTintList = ColorStateList.valueOf(ui.onSecondaryContainer)
            view.scaleType = ImageView.ScaleType.CENTER
        }
        val uri = FileActions.uriOf(r)
        val hasThumb = r.isImage || r.mime.startsWith("video/") || r.mime == "application/pdf"
        if (uri == null || loader == null || !hasThumb) {
            view.tag = null
            fallback()
            return
        }
        loader.load(uri, sizePx, view, fallback) { view.scaleType = ImageView.ScaleType.CENTER_CROP }
    }
}
