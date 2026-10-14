package dev.midfielder.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Starts the service after boot or an app update, but only once a PC has been paired. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (AppStorage(context).trustStore.all().isNotEmpty()) SyncService.start(context)
    }
}
