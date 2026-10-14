package dev.midfielder.android

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import dev.midfielder.core.crypto.PairingInvite
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Status, pairing and the one-time setup checklist. Plain framework views: the screen is shown rarely and
 * costs nothing when closed (its scope is cancelled in onDestroy).
 */
class MainActivity : Activity() {
    private val ui = MainScope()
    private var statusJob: Job? = null
    private lateinit var status: TextView
    private lateinit var capture: TextView
    private lateinit var checklist: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        SyncService.start(this)
        setContentView(buildUi())
        handleDeepLink(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleDeepLink(intent)
    }

    override fun onResume() {
        super.onResume()
        SyncEngine.current?.uiInForeground = true
        refreshChecklist()
        statusJob?.cancel()
        statusJob = ui.launch {
            var engine = SyncEngine.current
            while (engine == null) {
                delay(100)
                engine = SyncEngine.current
            }
            engine.uiInForeground = true
            engine.status.collect {
                status.text = engine.statusText()
                capture.text = "Phone → PC clipboard: ${engine.captureMode}"
            }
        }
    }

    override fun onPause() {
        SyncEngine.current?.uiInForeground = false
        statusJob?.cancel()
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // We have focus, so reading is allowed: pick up anything copied while we were away.
        if (hasFocus) SyncEngine.current?.let { e -> runCatching { ClipboardBridge(this).read() }.getOrNull()?.let(e::onLocalClipboard) }
    }

    override fun onDestroy() {
        ui.cancel()
        super.onDestroy()
    }

    // ---------------------------------------------------------------------------------------------

    private fun buildUi(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(24), dp(20), dp(24))
        }
        root.addView(text("Midfielder", 28f, bold = true))
        status = text("Starting…", 16f).also { root.addView(it) }
        capture = text("", 13f).also { root.addView(it) }

        root.addView(section("Pair & send"))
        root.addView(button("Pair with PC (scan QR code)") { startScan() })
        root.addView(button("Send clipboard to PC now") {
            val content = ClipboardBridge(this).read()
            val engine = SyncEngine.current
            if (content != null && engine != null) {
                engine.onLocalClipboard(content)
                toast("Sent")
            } else {
                toast("Nothing to send")
            }
        })
        root.addView(button("Send files to PC…") {
            startActivityForResult(
                Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
                    .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true),
                REQ_PICK_FILES,
            )
        })

        root.addView(section("Make it automatic (one-time setup)"))
        checklist = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(checklist)

        root.addView(section("Settings"))
        val storage = AppStorage(this)
        root.addView(switch("Sync clipboard", storage.syncClipboard) { on ->
            storage.syncClipboard = on
            SyncEngine.current?.clipSync?.enabled = on
        })
        root.addView(switch("Receive files from PC automatically", storage.acceptFiles) { storage.acceptFiles = it })
        root.addView(button("Forget paired PC") {
            AlertDialog.Builder(this).setMessage("Forget all paired PCs? You will need to scan a new code.")
                .setPositiveButton("Forget") { _, _ -> SyncEngine.current?.unpairAll() }
                .setNegativeButton("Cancel", null).show()
        })
        return ScrollView(this).apply { addView(root) }
    }

    private fun refreshChecklist() {
        if (!::checklist.isInitialized) return
        checklist.removeAllViews()
        val power = getSystemService(PowerManager::class.java)
        if (Build.VERSION.SDK_INT >= 33) {
            val granted = checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            checklist.addView(step("Show transfer notifications", granted) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIFICATIONS)
            })
        }
        checklist.addView(step("Stay connected in the background (no battery limits)", power.isIgnoringBatteryOptimizations(packageName)) {
            @Suppress("BatteryLife")
            startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        })
        checklist.addView(step("Allow \"Display over other apps\" (needed to send copies automatically)", Settings.canDrawOverlays(this)) {
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        })
        val readLogs = checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED
        checklist.addView(step("Detect copies silently (one-time ADB command, recommended)", readLogs) {
            val cmd = "adb shell pm grant $packageName android.permission.READ_LOGS"
            getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("adb", cmd))
            AlertDialog.Builder(this).setTitle("One-time setup from your PC")
                .setMessage(
                    "Enable USB debugging, connect the phone, and run:\n\n$cmd\n\n" +
                        "(Copied to your clipboard.) Then force-stop and reopen Midfielder.\n\n" +
                        "No PC handy? Use the accessibility option below instead.",
                )
                .setPositiveButton("OK", null).show()
        })
        checklist.addView(step("…or detect copies with accessibility", false, actionLabel = "Open") {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        })
        checklist.addView(text("Tip: add the \"Send clipboard\" tile to Quick Settings for a one-tap fallback.", 13f))
    }

    // ---------------------------------------------------------------------------------------------
    // Pairing

    private fun startScan() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
            return
        }
        startActivityForResult(Intent(this, ScanActivity::class.java), REQ_SCAN)
    }

    private fun handleDeepLink(intent: Intent?) {
        val data = intent?.data ?: return
        if (data.scheme == "midfielder" && data.host == "pair") confirmPairing(data.toString())
    }

    private fun confirmPairing(uri: String) {
        val invite = runCatching { PairingInvite.parse(uri) }.getOrElse {
            toast("That isn't a Midfielder pairing code")
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Pair with ${invite.name}?")
            .setMessage("Check that your PC shows this fingerprint:\n\n${invite.fingerprint.display()}")
            .setPositiveButton("Pair") { _, _ -> pair(invite) }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun pair(invite: PairingInvite) {
        ui.launch {
            var engine = SyncEngine.current
            while (engine == null) {
                delay(100)
                engine = SyncEngine.current
            }
            runCatching { engine.pair(invite) }
                .onSuccess { toast("Paired with ${it.name}") }
                .onFailure { toast("Pairing failed: ${it.message}") }
        }
    }

    @Deprecated("Framework API; this app has no AndroidX dependency")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode != RESULT_OK || data == null) return
        when (requestCode) {
            REQ_SCAN -> data.getStringExtra(ScanActivity.EXTRA_URI)?.let(::confirmPairing)
            REQ_PICK_FILES -> {
                val clip = data.clipData
                val uris = if (clip != null) (0 until clip.itemCount).map { clip.getItemAt(it).uri } else listOfNotNull(data.data)
                SyncService.sendUris(this, uris)
                toast("Sending ${uris.size} file(s)")
            }
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_CAMERA && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScan()
        refreshChecklist()
    }

    // ---------------------------------------------------------------------------------------------
    // Tiny view helpers

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun text(s: String, size: Float, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        if (bold) setTypeface(typeface, Typeface.BOLD)
        setPadding(0, dp(4), 0, dp(4))
    }

    private fun section(title: String) = text(title, 18f, bold = true).apply { setPadding(0, dp(20), 0, dp(6)) }

    private fun button(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        setOnClickListener { onClick() }
    }

    @Suppress("UseSwitchCompatOrMaterialCode")
    private fun switch(label: String, checked: Boolean, onChange: (Boolean) -> Unit) = Switch(this).apply {
        text = label
        isChecked = checked
        setPadding(0, dp(8), 0, dp(8))
        setOnCheckedChangeListener { _, on -> onChange(on) }
    }

    private fun step(label: String, done: Boolean, actionLabel: String = "Fix", onFix: () -> Unit) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        addView(text((if (done) "✓  " else "•  ") + label, 15f), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        if (!done) addView(button(actionLabel, onFix))
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_LONG).show()

    private companion object {
        const val REQ_SCAN = 1
        const val REQ_PICK_FILES = 2
        const val REQ_CAMERA = 3
        const val REQ_NOTIFICATIONS = 4
    }
}
