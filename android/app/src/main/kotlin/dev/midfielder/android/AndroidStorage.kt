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
        // Cancelled while bytes were still arriving: don't bring the file back.
        if (key in discarded) throw java.io.IOException("transfer cancelled")
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
        return u.toString()
    }

    override fun discard() {
        discarded += key
        pending.remove(key)?.let { runCatching { resolver.delete(it, null, null) } }
    }

    companion object {
        /** Partial entries by transfer key, so a resumed attempt finds the same file. */
        private val pending = ConcurrentHashMap<String, Uri>()

        /** Keys whose transfer was cancelled or failed (keys are unique per transfer, so this only grows a little). */
        private val discarded: MutableSet<String> = ConcurrentHashMap.newKeySet()

        /**
         * Deletes half-received files a previous run left behind (the app was killed mid-transfer: the resume
         * state lived in memory, so nothing can finish them). Hidden from other apps, but they took space for a
         * week until Android removed them. Ones from this run, or younger than [minAgeMs], are kept.
         */
        fun cleanStale(context: Context, minAgeMs: Long = 30 * 60_000L) {
            val resolver = context.contentResolver
            val ours = pending.values.toSet()
            val cutoffSec = (System.currentTimeMillis() - minAgeMs) / 1000
            val where = "${MediaStore.MediaColumns.IS_PENDING} = 1 AND ${MediaStore.MediaColumns.RELATIVE_PATH} LIKE ? AND ${MediaStore.MediaColumns.DATE_ADDED} < ?"
            val args = arrayOf(Environment.DIRECTORY_DOWNLOADS + "/Midfielder%", cutoffSec.toString())
            val base = MediaStore.Downloads.EXTERNAL_CONTENT_URI
            val stale = ArrayList<Uri>()
            runCatching {
                val cursor = if (android.os.Build.VERSION.SDK_INT >= 30) {
                    resolver.query(
                        base, arrayOf(MediaStore.MediaColumns._ID),
                        android.os.Bundle().apply {
                            putString(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION, where)
                            putStringArray(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, args)
                            putInt(MediaStore.QUERY_ARG_MATCH_PENDING, MediaStore.MATCH_INCLUDE)
                        },
                        null,
                    )
                } else {
                    @Suppress("DEPRECATION")
                    resolver.query(MediaStore.setIncludePending(base), arrayOf(MediaStore.MediaColumns._ID), where, args, null)
                }
                cursor?.use { c -> while (c.moveToNext()) stale += android.content.ContentUris.withAppendedId(base, c.getLong(0)) }
            }.onFailure { EventLog.error("Looking for unfinished downloads failed", it) }
            val gone = stale.filter { it !in ours }.count { runCatching { resolver.delete(it, null, null) > 0 }.getOrDefault(false) }
            if (gone > 0) EventLog.add("Removed $gone unfinished download(s) from an earlier run")
        }

        /** The name MediaStore actually gave the file, or null if it is gone. */
        fun displayName(context: Context, uri: Uri): String? =
            context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst()) c.getString(0) else null
            }
    }
}

/** Sends a content:// or file:// URI (from the share sheet or a picker). */
class UriFileSource(context: Context, val uri: Uri) : FileSource {
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
