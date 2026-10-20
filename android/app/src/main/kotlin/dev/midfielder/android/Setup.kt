package dev.midfielder.android

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings

/** The one-time setup for automatic phone → PC copying, as a list the UI can show or count. */
object Setup {
    class Step(val label: String, val detail: String, val done: Boolean, val action: String, val run: (Activity) -> Unit)

    const val REQ_NOTIFICATIONS = 4
    const val ADB_GRANT = "adb shell pm grant dev.midfielder android.permission.READ_LOGS"
    const val ADB_LOGS = "adb logcat -s Midfielder"

    fun steps(a: Activity): List<Step> {
        val steps = ArrayList<Step>()
        if (Build.VERSION.SDK_INT >= 33) {
            steps += Step(
                "Notifications", "Shows transfer progress and received files",
                a.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED, "Allow",
            ) { it.requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIFICATIONS) }
        }
        steps += Step(
            "No battery limits", "Keeps the connection alive in the background",
            a.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(a.packageName), "Fix",
        ) {
            @Suppress("BatteryLife")
            it.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${it.packageName}")))
        }
        steps += Step(
            "Display over other apps", "Lets Midfielder read a copy for an instant in the background",
            Settings.canDrawOverlays(a), "Allow",
        ) { it.startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${it.packageName}"))) }

        val readLogs = a.checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED
        val accessibility = isAccessibilityOn(a)
        val approved = SyncEngine.current?.logDetectionActive == true
        steps += Step(
            "Detect copies automatically",
            when {
                approved -> "On (log access approved)"
                accessibility -> "On (accessibility)"
                readLogs -> "Tap Ask, then \"Allow\" when Android asks"
                else -> "One-time ADB command, or turn on accessibility"
            },
            approved || accessibility, if (readLogs) "Ask" else "Set up",
        ) { act ->
            if (readLogs) {
                SyncEngine.current?.onUiResumed()
            } else {
                showDetectionHelp(act)
            }
        }
        return steps
    }

    fun showDetectionHelp(a: Activity) {
        AlertDialog.Builder(a).setTitle("Detect copies automatically")
            .setMessage(
                "Best: from a PC with USB debugging on, run once:\n\n$ADB_GRANT\n\n" +
                    "then reopen Midfielder and tap \"Allow\" when Android asks.\n\n" +
                    "No PC handy? Turn on Midfielder in Accessibility instead.",
            )
            .setPositiveButton("Copy command") { _, _ -> copy(a, ADB_GRANT) }
            .setNeutralButton("Accessibility") { _, _ -> a.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
            .setNegativeButton("Close", null)
            .show()
    }

    fun copy(a: Activity, text: String) {
        a.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Midfielder", text))
    }

    fun isAccessibilityOn(a: Activity): Boolean {
        val enabled = Settings.Secure.getString(a.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        return enabled.contains("${a.packageName}/") && enabled.contains(CopyAccessibilityService::class.java.simpleName)
    }
}
