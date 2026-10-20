package dev.midfielder.android

import android.content.Context
import android.content.Intent
import android.content.pm.ShortcutInfo
import android.content.pm.ShortcutManager
import android.graphics.drawable.Icon
import android.os.Build
import dev.midfielder.core.net.PairedDevice

/**
 * Direct Share: one long-lived sharing shortcut per paired PC, so "Send to <PC>" shows in the share sheet's
 * top row. Updated on start, pair and forget (rare), never on a timer.
 */
object ShareShortcuts {
    const val CATEGORY = "dev.midfielder.SHARE_TARGET"
    private const val PREFIX = "pc-"

    fun idFor(device: PairedDevice): String = PREFIX + device.fingerprint.hex.take(16)

    fun update(context: Context, devices: List<PairedDevice>) {
        runCatching {
            val sm = context.getSystemService(ShortcutManager::class.java) ?: return
            val wanted = devices.associateBy(::idFor)
            val stale = sm.dynamicShortcuts.map { it.id }.filter { it.startsWith(PREFIX) && it !in wanted }
            if (stale.isNotEmpty()) {
                if (Build.VERSION.SDK_INT >= 30) sm.removeLongLivedShortcuts(stale) else sm.removeDynamicShortcuts(stale)
            }
            for ((id, d) in wanted) {
                val shortcut = ShortcutInfo.Builder(context, id)
                    .setShortLabel(d.name.take(24))
                    .setLongLabel("Send to ${d.name}")
                    .setIcon(Icon.createWithResource(context, R.mipmap.ic_share_pc))
                    .setCategories(setOf(CATEGORY))
                    .setLongLived(true)
                    // Opened from the launcher's long-press menu: just show the app.
                    .setIntent(Intent(context, MainActivity::class.java).setAction(Intent.ACTION_VIEW))
                    .build()
                if (Build.VERSION.SDK_INT >= 30) sm.pushDynamicShortcut(shortcut) else sm.addDynamicShortcuts(listOf(shortcut))
            }
        }.onFailure { EventLog.error("Updating share shortcuts failed", it) }
    }
}
