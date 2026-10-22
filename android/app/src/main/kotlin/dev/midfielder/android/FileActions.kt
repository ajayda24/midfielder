package dev.midfielder.android

import android.app.Activity
import android.app.DownloadManager
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ContentUris
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.widget.Toast
import dev.midfielder.core.history.TransferRecord

/** Opening, sharing and finding files from the history. */
object FileActions {
    fun uriOf(r: TransferRecord): Uri? = r.location.takeIf { it.startsWith("content://") || it.startsWith("file://") }?.let(Uri::parse)

    /** Opens the file in the app Android picks for its type (gallery for photos, PDF viewer, …). */
    fun open(a: Activity, r: TransferRecord) {
        val uri = uriOf(r) ?: return toast(a, "This file isn't on the phone")
        val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, r.mime.ifEmpty { "*/*" })
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            a.startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            toast(a, "No app on this phone opens ${r.mime.ifEmpty { "this file" }}")
        } catch (e: SecurityException) {
            toast(a, "This file can no longer be opened")
        }
    }

    fun share(a: Activity, records: List<TransferRecord>) {
        val uris = records.mapNotNull(::uriOf)
        if (uris.isEmpty()) return
        val intent = if (uris.size == 1) {
            Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0]).setType(records[0].mime.ifEmpty { "*/*" })
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris)).setType("*/*")
        }
        intent.clipData = ClipData.newRawUri("files", uris[0]).apply { for (u in uris.drop(1)) addItem(ClipData.Item(u)) }
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        runCatching { a.startActivity(Intent.createChooser(intent, null)) }
    }

    /** Opens Download/Midfielder in the Files app, falling back to the Downloads list. */
    fun openFolder(a: Activity) {
        val folder = DocumentsContract.buildDocumentUri("com.android.externalstorage.documents", "primary:Download/Midfielder")
        val attempts = listOf(
            Intent(Intent.ACTION_VIEW).setDataAndType(folder, DocumentsContract.Document.MIME_TYPE_DIR),
            Intent(Intent.ACTION_VIEW).setDataAndType(folder, "resource/folder"),
            Intent(DownloadManager.ACTION_VIEW_DOWNLOADS),
        )
        // No grant flag: the folder belongs to the system's documents provider, and granting a URI we don't own
        // throws, which made the first (best) attempt always fail.
        for (i in attempts) {
            if (runCatching { a.startActivity(i) }.isSuccess) return
        }
        toast(a, "No file manager found")
    }

    /** Ids of received MediaStore files that no longer exist (deleted in another app). */
    fun missing(context: Context, records: List<TransferRecord>): List<String> {
        val byId = records.mapNotNull { r ->
            val uri = uriOf(r) ?: return@mapNotNull null
            if (uri.authority != MediaStore.AUTHORITY) return@mapNotNull null
            runCatching { ContentUris.parseId(uri) }.getOrNull()?.let { it to r.id }
        }
        if (byId.isEmpty()) return emptyList()
        val present = HashSet<Long>()
        byId.chunked(400).forEach { chunk ->
            val sel = "${MediaStore.MediaColumns._ID} IN (${chunk.joinToString(",") { it.first.toString() }})"
            context.contentResolver.query(
                MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL), arrayOf(MediaStore.MediaColumns._ID), sel, null, null,
            )?.use { c -> while (c.moveToNext()) present += c.getLong(0) }
                ?: return emptyList() // can't tell: keep everything
        }
        return byId.filter { it.first !in present }.map { it.second }
    }

    private fun toast(c: Context, s: String) = Toast.makeText(c, s, Toast.LENGTH_SHORT).show()
}
