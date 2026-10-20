package dev.midfielder.android

import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.os.Environment
import android.provider.MediaStore
import dev.midfielder.core.history.Direction
import dev.midfielder.core.history.RecordStatus
import dev.midfielder.core.history.TransferRecord
import kotlin.concurrent.thread

/** Debug builds only (see MainActivity.EXTRA_DEMO): sample history and stats so CI screenshots show real screens. */
object Demo {
    fun seed(context: Context) {
        val app = context.applicationContext
        thread(name = "demo-seed") {
            val store = HistoryStore.get(app)
            if (store.records().any { it.id.startsWith("demo-") }) return@thread
            val now = System.currentTimeMillis()
            val colors = intArrayOf(0xFF2E7D6B.toInt(), 0xFF5C8DC4.toInt(), 0xFFE0A82E.toInt(), 0xFFC25B56.toInt(), 0xFF8E6CC0.toInt(), 0xFF3B9C9C.toInt())
            val records = ArrayList<TransferRecord>()
            colors.forEachIndexed { i, c ->
                val uri = runCatching { image(app, "IMG_20261020_0$i.png", c) }.getOrNull() ?: return@forEachIndexed
                records += TransferRecord("demo-img$i", Direction.RECEIVED, "IMG_20261020_0$i.png", "image/png", 245_000L + i * 13_000,
                    "Ajay's Laptop", now - i * 3_600_000L, RecordStatus.DONE, uri.toString())
            }
            records += TransferRecord("demo-doc", Direction.SENT, "Quarterly report.pdf", "application/pdf", 1_480_000, "Ajay's Laptop",
                now - 26 * 3_600_000L, RecordStatus.DONE, "")
            records += TransferRecord("demo-zip", Direction.RECEIVED, "project-assets.zip", "application/zip", 48_200_000, "Ajay's Laptop",
                now - 50 * 3_600_000L, RecordStatus.DONE, "")
            records += TransferRecord("demo-mov", Direction.SENT, "holiday.mov", "video/quicktime", 812_000_000, "Ajay's Laptop",
                now - 9 * 86_400_000L, RecordStatus.FAILED, "")
            store.add(records)
            for (d in 0..6) {
                repeat((d * 3 + 2) % 9) { store.stats.recordClip(Direction.SENT, now - d * 86_400_000L) }
                store.stats.recordFiles(Direction.RECEIVED, d % 3, 1_500_000L * d, now - d * 86_400_000L)
            }
            store.recordClip(Direction.RECEIVED)
            SyncEngine.current?.transfers?.let {
                it.start("demo-transfer", Direction.RECEIVED, "VID_20261020.mp4", "Ajay's Laptop", 250_000_000)
                it.progress("demo-transfer", 160_000_000, 250_000_000)
            }
        }
    }

    private fun image(context: Context, name: String, color: Int): android.net.Uri? {
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.MIME_TYPE, "image/png")
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Midfielder")
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        val bmp = Bitmap.createBitmap(480, 360, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bmp)
        canvas.drawColor(color)
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.color = Color.argb(110, 255, 255, 255) }
        canvas.drawCircle(300f, 140f, 110f, p)
        canvas.drawCircle(130f, 270f, 70f, p)
        resolver.openOutputStream(uri)?.use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
        bmp.recycle()
        return uri
    }
}
