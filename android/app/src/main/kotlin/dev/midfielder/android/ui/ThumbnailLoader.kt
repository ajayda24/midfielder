package dev.midfielder.android.ui

import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.os.CancellationSignal
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.util.Size
import android.widget.ImageView
import java.util.WeakHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Loads MediaStore/provider thumbnails off the main thread into a memory-bounded cache (1/16 of the heap).
 * A recycled view cancels its previous request. [close] stops the workers and drops every bitmap.
 */
class ThumbnailLoader(context: Context) {
    private val resolver = context.applicationContext.contentResolver
    private val main = Handler(Looper.getMainLooper())
    private val workers: ExecutorService = Executors.newFixedThreadPool(2)
    private val inFlight = WeakHashMap<ImageView, CancellationSignal>()
    private val cache = object : LruCache<String, Bitmap>((Runtime.getRuntime().maxMemory() / 16 / 1024).toInt()) {
        override fun sizeOf(key: String, value: Bitmap) = value.byteCount / 1024
    }

    @Volatile
    private var closed = false

    /** Files without a thumbnail (deleted, unreadable, no preview): not asked again on every scroll. */
    private val failed: MutableSet<String> = java.util.Collections.synchronizedSet(HashSet())

    /** Shows the thumbnail of [uri] in [view] ([loaded] runs after), or [fallback]'s placeholder if there is none. */
    fun load(uri: Uri, sizePx: Int, view: ImageView, fallback: () -> Unit, loaded: () -> Unit) {
        val key = "$uri@$sizePx"
        // Rebinding the same file (a list update, a selection change): keep the picture, no placeholder flash.
        if (view.tag == key && view.drawable is android.graphics.drawable.BitmapDrawable && !inFlight.containsKey(view)) return
        inFlight.remove(view)?.cancel()
        view.tag = key
        cache.get(key)?.let {
            view.setImageBitmap(it)
            view.imageTintList = null
            loaded()
            return
        }
        fallback()
        if (closed || key in failed) return
        val signal = CancellationSignal()
        inFlight[view] = signal
        workers.execute {
            val bmp = if (signal.isCanceled) null else runCatching { resolver.loadThumbnail(uri, Size(sizePx, sizePx), signal) }.getOrNull()
            if (bmp != null) cache.put(key, bmp) else if (!signal.isCanceled) synchronized(failed) { failed += key }
            main.post {
                if (closed || view.tag != key) return@post
                inFlight.remove(view)
                if (bmp != null) {
                    view.setImageBitmap(bmp)
                    view.imageTintList = null
                    loaded()
                }
            }
        }
    }

    fun close() {
        closed = true
        for (s in inFlight.values) s.cancel()
        inFlight.clear()
        workers.shutdownNow()
        cache.evictAll()
    }
}
