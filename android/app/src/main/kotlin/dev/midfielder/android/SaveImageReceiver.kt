package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.os.Environment
import android.provider.MediaStore
import android.widget.Toast

/** "Save to gallery" on the image-from-PC notification: copies the clipboard image into Pictures/Midfielder. */
class SaveImageReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val source = intent.data ?: return
        val app = context.applicationContext
        val pending = goAsync()
        Thread {
            val ok = runCatching {
                val resolver = app.contentResolver
                val values = ContentValues().apply {
                    put(MediaStore.Images.Media.DISPLAY_NAME, "Midfielder-${System.currentTimeMillis()}.png")
                    put(MediaStore.Images.Media.MIME_TYPE, "image/png")
                    put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/Midfielder")
                    put(MediaStore.Images.Media.IS_PENDING, 1)
                }
                val target = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: error("insert failed")
                try {
                    resolver.openInputStream(source)!!.use { input ->
                        resolver.openOutputStream(target)!!.use { input.copyTo(it) }
                    }
                    resolver.update(target, ContentValues().apply { put(MediaStore.Images.Media.IS_PENDING, 0) }, null, null)
                } catch (e: Exception) {
                    resolver.delete(target, null, null)
                    throw e
                }
            }.onFailure { EventLog.error("Saving image to gallery failed", it) }.isSuccess
            Notifications(app).cancelClipImage()
            android.os.Handler(android.os.Looper.getMainLooper()).post {
                Toast.makeText(app, if (ok) "Saved to Pictures/Midfielder" else "Couldn't save the image", Toast.LENGTH_SHORT).show()
                pending.finish()
            }
        }.start()
    }
}
