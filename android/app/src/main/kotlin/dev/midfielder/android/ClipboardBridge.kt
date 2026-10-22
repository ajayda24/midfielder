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

    /** What was taken off the clipboard while we had focus: text is ready, an image is loaded later off the UI thread. */
    sealed interface Grab {
        class Ready(val content: ClipContent) : Grab
        class Image(val uri: Uri, val mime: String, val sensitive: Boolean) : Grab
    }


    /**
     * Grabs the clipboard (needs window focus). Cheap and quiet: unless [force], a clip already seen (same
     * timestamp) isn't read again, so returning to the app doesn't re-send it or trigger Android's "pasted" toast.
     */
    fun grab(force: Boolean = false): Grab? {
        val stamp = clipboard.primaryClipDescription?.timestamp ?: return null
        if (!force && stamp == lastStamp) return null
        val clip = clipboard.primaryClip ?: return null
        lastStamp = stamp // process-wide: every reader (activity, engine, UI) shares it
        if (clip.itemCount == 0) return null
        val sensitive = isSensitive(clip.description)
        val item = clip.getItemAt(0)
        val uri = item.uri
        if (uri != null) {
            if (uri.authority == ClipImageProvider.AUTHORITY) return null // our own write
            val mime = runCatching { app.contentResolver.getType(uri) }.getOrNull()
            if (mime != null && mime.startsWith("image/")) return Grab.Image(uri, mime, sensitive)
        }
        // Only real text. coerceToText() would read a whole content URI into memory.
        val text = item.text?.toString()
        if (text.isNullOrEmpty()) return null
        return Grab.Ready(ClipContent.Text(text, sensitive))
    }

    /** Turns a grab into content; images are decoded here (call off the main thread for those). */
    fun load(grab: Grab): ClipContent? = when (grab) {
        is Grab.Ready -> grab.content
        is Grab.Image -> readImage(grab.uri, grab.mime, grab.sensitive)
    }

    /** Grab and load in one go (text only callers, or already off the main thread). */
    fun read(force: Boolean = true): ClipContent? = grab(force)?.let(::load)

    /** Puts [content] on the clipboard. For an image, returns the content URI it is served from. */
    fun write(content: ClipContent): Uri? {
        suppressUntil = SystemClock.elapsedRealtime() + SUPPRESS_MS
        val imageUri = (content as? ClipContent.Image)?.let { ClipImageProvider.store(app, it.png) }
        val clip = when (content) {
            is ClipContent.Text -> ClipData.newPlainText("Midfielder", content.text)
            is ClipContent.Image -> ClipData.newUri(app.contentResolver, "Midfielder image", imageUri)
        }
        if (content.sensitive) {
            // Hides the preview in the system clipboard UI (Android 13+ honors this extra).
            clip.description.extras = PersistableBundle().apply { putBoolean(EXTRA_IS_SENSITIVE, true) }
        }
        clipboard.setPrimaryClip(clip)
        return imageUri
    }

    /** Puts an image Midfielder already stored in [ClipImageProvider] on the clipboard. */
    fun writeImage(uri: Uri) {
        suppressUntil = SystemClock.elapsedRealtime() + SUPPRESS_MS
        clipboard.setPrimaryClip(ClipData.newUri(app.contentResolver, "Midfielder image", uri))
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
        @Volatile
        var lastStamp = -1L
        const val SUPPRESS_MS = 1_500L
        const val MAX_SIDE = 2048 // plenty for a pasted picture; a full-size photo would be ~20 MB as PNG
        const val MAX_PIXELS = 50_000_000L
        val EXTRA_IS_SENSITIVE: String =
            if (Build.VERSION.SDK_INT >= 33) ClipDescription.EXTRA_IS_SENSITIVE else "android.content.extra.IS_SENSITIVE"
    }
}
