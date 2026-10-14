package dev.midfielder.android

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.PersistableBundle
import android.os.SystemClock
import dev.midfielder.core.Limits
import dev.midfielder.core.clip.ClipContent
import java.io.ByteArrayOutputStream

/** Reads and writes the Android clipboard. Reading only works while one of our windows has focus. */
class ClipboardBridge(context: Context) {
    private val app = context.applicationContext
    private val clipboard = app.getSystemService(ClipboardManager::class.java)

    /** Clipboard changes before this time were caused by our own writes. */
    @Volatile
    var suppressUntil: Long = 0L
        private set

    val isSuppressed: Boolean get() = SystemClock.elapsedRealtime() < suppressUntil

    fun read(): ClipContent? {
        val clip = clipboard.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        val sensitive = isSensitive(clip.description)
        val item = clip.getItemAt(0)
        val uri = item.uri
        if (uri != null) {
            if (uri.authority == ClipImageProvider.AUTHORITY) return null // our own write
            val mime = app.contentResolver.getType(uri)
            if (mime != null && mime.startsWith("image/")) return readImage(uri, mime, sensitive)
        }
        // Only real text. coerceToText() would read a whole content URI into memory.
        val text = item.text?.toString()
        if (text.isNullOrEmpty()) return null
        return ClipContent.Text(text, sensitive)
    }

    fun write(content: ClipContent) {
        suppressUntil = SystemClock.elapsedRealtime() + SUPPRESS_MS
        val clip = when (content) {
            is ClipContent.Text -> ClipData.newPlainText("Midfielder", content.text)
            is ClipContent.Image -> ClipData.newUri(app.contentResolver, "Midfielder image", ClipImageProvider.store(app, content.png))
        }
        if (content.sensitive) {
            // Hides the preview in the system clipboard UI (Android 13+ honors this extra).
            clip.description.extras = PersistableBundle().apply { putBoolean(EXTRA_IS_SENSITIVE, true) }
        }
        clipboard.setPrimaryClip(clip)
    }

    fun addListener(listener: ClipboardManager.OnPrimaryClipChangedListener) = clipboard.addPrimaryClipChangedListener(listener)
    fun removeListener(listener: ClipboardManager.OnPrimaryClipChangedListener) = clipboard.removePrimaryClipChangedListener(listener)

    private fun isSensitive(desc: ClipDescription): Boolean = desc.extras?.getBoolean(EXTRA_IS_SENSITIVE, false) == true

    /** PNG passes through untouched; anything else is decoded (downsampled if huge) and re-encoded. */
    private fun readImage(uri: Uri, mime: String, sensitive: Boolean): ClipContent? = runCatching {
        val resolver = app.contentResolver
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        val w = bounds.outWidth
        val h = bounds.outHeight
        if (w <= 0 || h <= 0 || w.toLong() * h > MAX_PIXELS) return null
        if (mime == "image/png") {
            val bytes = resolver.openInputStream(uri)?.use { readBounded(it, Limits.MAX_INLINE_CLIP_BYTES) }
            if (bytes != null) return ClipContent.Image(bytes, sensitive)
        }
        var sample = 1
        while (maxOf(w, h) / sample > MAX_SIDE) sample *= 2
        val bitmap = resolver.openInputStream(uri)?.use {
            BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
        } ?: return null
        try {
            val out = ByteArrayOutputStream()
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
            ClipContent.Image(out.toByteArray(), sensitive)
        } finally {
            bitmap.recycle()
        }
    }.getOrNull()

    /** Reads at most [limit] bytes; returns null if the stream is longer. */
    private fun readBounded(input: java.io.InputStream, limit: Int): ByteArray? {
        val out = ByteArrayOutputStream()
        val buf = ByteArray(64 * 1024)
        var total = 0
        while (true) {
            val n = input.read(buf)
            if (n < 0) return out.toByteArray()
            total += n
            if (total > limit) return null
            out.write(buf, 0, n)
        }
    }

    private companion object {
        const val SUPPRESS_MS = 1_500L
        const val MAX_SIDE = 4096
        const val MAX_PIXELS = 50_000_000L
        val EXTRA_IS_SENSITIVE: String =
            if (Build.VERSION.SDK_INT >= 33) ClipDescription.EXTRA_IS_SENSITIVE else "android.content.extra.IS_SENSITIVE"
    }
}
