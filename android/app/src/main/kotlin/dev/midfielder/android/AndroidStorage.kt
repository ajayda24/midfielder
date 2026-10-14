package dev.midfielder.android

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.provider.OpenableColumns
import dev.midfielder.core.transfer.FileSink
import dev.midfielder.core.transfer.FileSource
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.ConcurrentHashMap

/**
 * Receives into Download/Midfielder through MediaStore: no storage permission needed. While incomplete the
 * entry is IS_PENDING (hidden from other apps, and auto-deleted by Android after a week if abandoned).
 */
class MediaStoreFileSink(context: Context, private val key: String, private val displayName: String, private val mime: String) : FileSink {
    private val resolver = context.applicationContext.contentResolver

    private fun uri(create: Boolean): Uri? = pending[key] ?: if (!create) null else {
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, displayName)
            put(MediaStore.Downloads.MIME_TYPE, mime.ifEmpty { "application/octet-stream" })
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Midfielder")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)?.also { pending[key] = it }
            ?: throw java.io.IOException("could not create download entry")
    }

    override fun partialLength(): Long {
        val u = uri(false) ?: return 0
        return runCatching { resolver.openFileDescriptor(u, "r")?.use { it.statSize } ?: 0L }.getOrDefault(0L)
    }

    override fun openPartial(): InputStream =
        resolver.openInputStream(uri(false) ?: throw java.io.FileNotFoundException(key)) ?: throw java.io.FileNotFoundException(key)

    override fun openAt(offset: Long): OutputStream {
        val pfd = resolver.openFileDescriptor(uri(true)!!, "rw") ?: throw java.io.IOException("cannot open download")
        val out = ParcelFileDescriptor.AutoCloseOutputStream(pfd)
        try {
            out.channel.truncate(offset)
            out.channel.position(offset)
        } catch (e: Exception) {
            out.close()
            throw e
        }
        return out
    }

    override fun commit(): String {
        val u = uri(false) ?: throw java.io.IOException("nothing to commit")
        resolver.update(u, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
        pending.remove(key)
        return "Download/Midfielder/$displayName"
    }

    override fun discard() {
        pending.remove(key)?.let { runCatching { resolver.delete(it, null, null) } }
    }

    private companion object {
        /** Partial entries by transfer key, so a resumed attempt finds the same file. */
        val pending = ConcurrentHashMap<String, Uri>()
    }
}

/** Sends a content:// or file:// URI (from the share sheet or a picker). */
class UriFileSource(context: Context, private val uri: Uri) : FileSource {
    private val resolver = context.applicationContext.contentResolver
    override val name: String
    override val size: Long
    override val mime: String = resolver.getType(uri) ?: "application/octet-stream"
    override val modifiedMs: Long = System.currentTimeMillis()

    init {
        var n: String? = null
        var s = -1L
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                n = c.getString(0)
                if (!c.isNull(1)) s = c.getLong(1)
            }
        }
        if (s < 0) s = resolver.openFileDescriptor(uri, "r")?.use { it.statSize } ?: -1
        require(s >= 0) { "unknown size for $uri" }
        name = n ?: uri.lastPathSegment ?: "file"
        size = s
    }

    override fun open(): InputStream = resolver.openInputStream(uri) ?: throw java.io.FileNotFoundException(uri.toString())
}
