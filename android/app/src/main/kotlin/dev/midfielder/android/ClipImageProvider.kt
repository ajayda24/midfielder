package dev.midfielder.android

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/**
 * Serves clipboard images received from the PC. Only the most recent few are kept on disk, so the cache
 * can't grow without bound.
 */
class ClipImageProvider : ContentProvider() {
    override fun onCreate(): Boolean = true

    override fun getType(uri: Uri): String = "image/png"

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (mode != "r") throw SecurityException("read-only")
        val file = fileFor(context ?: throw FileNotFoundException(), uri) ?: throw FileNotFoundException(uri.toString())
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, args: Array<out String>?, sort: String?): Cursor? {
        val file = fileFor(context ?: return null, uri) ?: return null
        return MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)).apply {
            addRow(arrayOf<Any>(file.name, file.length()))
        }
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, args: Array<out String>?): Int = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, args: Array<out String>?): Int = 0

    companion object {
        const val AUTHORITY = "dev.midfielder.clipimages"
        private const val KEEP = 3

        private fun dir(context: Context) = File(context.cacheDir, "clip").apply { mkdirs() }

        fun store(context: Context, png: ByteArray): Uri = keep(context) { it.writeBytes(png) }

        /** Takes over a received PNG file (a rename, same disk): a 60 MB image never has to be read into memory. */
        fun storeFile(context: Context, png: File): Uri = keep(context) { target ->
            if (!png.renameTo(target)) {
                png.copyTo(target, overwrite = true)
                png.delete()
            }
        }

        private fun keep(context: Context, write: (File) -> Unit): Uri {
            val d = dir(context)
            val file = File(d, "clip-${System.currentTimeMillis()}.png")
            write(file)
            d.listFiles()?.sortedByDescending { it.lastModified() }?.drop(KEEP)?.forEach { it.delete() }
            return Uri.Builder().scheme("content").authority(AUTHORITY).appendPath(file.name).build()
        }

        private fun fileFor(context: Context, uri: Uri): File? {
            val name = uri.lastPathSegment ?: return null
            if (!name.matches(Regex("clip-\\d+\\.png"))) return null // no path traversal
            return File(dir(context), name).takeIf { it.isFile }
        }
    }
}
