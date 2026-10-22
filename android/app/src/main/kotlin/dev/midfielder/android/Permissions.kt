package dev.midfielder.android

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import dev.midfielder.core.crypto.PairingInvite
import java.io.IOException
import java.net.ConnectException
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * Runtime permissions with memory: Android stops showing its dialog after a permission was refused twice
 * ("don't ask again") and then answers "denied" silently, so a Grant or Allow button would do nothing. We remember
 * which permissions were asked, and once one is blocked the button opens Midfielder's page in Android settings.
 */
object Permissions {
    private fun prefs(context: Context) = context.getSharedPreferences("permissions", Context.MODE_PRIVATE)

    fun granted(context: Context, permission: String): Boolean =
        context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

    fun request(activity: Activity, permissions: Array<String>, requestCode: Int) {
        if (permissions.isEmpty()) return
        prefs(activity).edit().apply { for (p in permissions) putBoolean(p, true) }.apply()
        activity.requestPermissions(permissions, requestCode)
    }

    /** Asked before, still denied, and Android won't show the dialog again. */
    fun blocked(activity: Activity, permission: String): Boolean =
        !granted(activity, permission) && prefs(activity).getBoolean(permission, false) &&
            !activity.shouldShowRequestPermissionRationale(permission)

    /** Asks for what's missing, or opens Android's settings page when Android won't ask anymore. */
    fun requestOrOpenSettings(activity: Activity, permissions: List<String>, requestCode: Int) {
        val missing = permissions.filterNot { granted(activity, it) }
        if (missing.isEmpty()) return
        if (missing.all { blocked(activity, it) }) openAppSettings(activity) else request(activity, missing.toTypedArray(), requestCode)
    }

    fun openAppSettings(context: Context) {
        openSafely(context, Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null)))
    }

    /**
     * Opens a system screen; some aren't on every phone (Go editions, some OEM builds), so fall back to Midfielder's
     * app info page instead of crashing. Returns false if nothing could be opened.
     */
    fun openSafely(context: Context, intent: Intent, fallback: Intent? = null): Boolean {
        val flags = if (context is Activity) 0 else Intent.FLAG_ACTIVITY_NEW_TASK
        for (i in listOfNotNull(intent, fallback, Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null)))) {
            try {
                context.startActivity(i.addFlags(flags))
                return true
            } catch (_: ActivityNotFoundException) {
            } catch (_: SecurityException) {
            }
        }
        return false
    }

    /**
     * Android 13+ greys out Accessibility and notification access for apps installed outside a store
     * ("Restricted setting") until it's allowed in App info.
     */
    val restrictedSettingsHint: String?
        get() = if (android.os.Build.VERSION.SDK_INT >= 33) {
            "Greyed out? Open App info, tap ⋮ (top right), then \"Allow restricted settings\", and try again."
        } else {
            null
        }
}

/** Pairing links come from a QR code on your own PC: they must point into the local network. */
object PairLinks {
    fun isLocal(invite: PairingInvite): Boolean = invite.addresses.all { isPrivateHost(it.host) }

    private fun isPrivateHost(host: String): Boolean {
        // Only literal addresses: a name would need a DNS lookup that a web page could control.
        val literal = host.trim('[', ']')
        if (!literal.all { it.isDigit() || it == '.' || it == ':' || it.lowercaseChar() in 'a'..'f' || it == '%' }) return false
        val addr = runCatching { InetAddress.getByName(literal.substringBefore('%')) }.getOrNull() ?: return false
        return addr.isSiteLocalAddress || addr.isLinkLocalAddress || addr.isLoopbackAddress || isPrivate(addr)
    }

    /** The ranges Java's checks miss: IPv6 unique local and carrier-grade NAT. */
    fun isPrivate(addr: InetAddress): Boolean {
        val b = addr.address
        // IPv6 unique local (fc00::/7) and the 100.64/10 carrier-grade range used by phone hotspots.
        return (b.size == 16 && (b[0].toInt() and 0xFE) == 0xFC) ||
            (b.size == 4 && b[0].toInt() and 0xFF == 100 && (b[1].toInt() and 0xC0) == 64)
    }

    /** What went wrong, in words the user can act on. */
    fun describeFailure(invite: PairingInvite, e: Throwable): String {
        val cause = generateSequence(e) { it.cause }.toList()
        return when {
            cause.any { it is SocketTimeoutException || it is ConnectException } ->
                "Couldn't reach ${invite.name}. Make sure the phone and the PC are on the same Wi-Fi, and Midfielder is open on the PC."
            cause.any { it.message?.contains("proof", ignoreCase = true) == true || it.message?.contains("pairing window", ignoreCase = true) == true } ->
                "${invite.name} didn't accept this code. Open Pair a phone on the PC again and scan the new code."
            cause.any { it is IOException } -> "The connection to ${invite.name} dropped. Try again."
            else -> "Pairing didn't finish. Try again."
        }
    }
}
