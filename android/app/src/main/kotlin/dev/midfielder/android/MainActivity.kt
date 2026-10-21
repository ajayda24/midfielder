package dev.midfielder.android

import android.Manifest
import android.app.RecoverableSecurityException
import android.content.pm.ApplicationInfo
import android.content.Intent
import android.content.IntentSender
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.FrameLayout
import android.widget.LinearLayout
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.navigation.NavigationBarView
import com.google.android.material.snackbar.Snackbar
import dev.midfielder.android.ui.DeveloperTab
import dev.midfielder.android.ui.FilesTab
import dev.midfielder.android.ui.HomeTab
import dev.midfielder.android.ui.SettingsTab
import dev.midfielder.android.ui.Tab
import dev.midfielder.android.ui.Ui
import dev.midfielder.core.crypto.PairingInvite
import dev.midfielder.core.history.TransferRecord
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * The app's only regular screen: four tabs (Home, Files, Settings, Developer) over a bottom bar. Framework views
 * only. A tab's view and its coroutines exist only while it's shown and the activity is resumed.
 */
class MainActivity : AppCompatActivity() {
    lateinit var ui: Ui
        private set

    private val scope = MainScope()
    private var tabJob: Job? = null
    private var tab: Tab? = null
    private var tabName = TAB_HOME
    private var resumed = false
    private lateinit var content: FrameLayout
    private lateinit var nav: BottomNavigationView
    private val pendingDelete = ArrayList<String>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ui = Ui(this)
        SyncService.start(this)
        setContentView(buildFrame())
        showTab(savedInstanceState?.getString(STATE_TAB) ?: intent.getStringExtra(EXTRA_TAB) ?: TAB_HOME)
        handleIntent(intent)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    tab?.onBack() == true -> Unit
                    tabName != TAB_HOME -> showTab(TAB_HOME)
                    else -> {
                        isEnabled = false
                        onBackPressedDispatcher.onBackPressed()
                        isEnabled = true
                    }
                }
            }
        })
        if (intent.getBooleanExtra(EXTRA_DEMO, false) && (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0) {
            Demo.seed(this)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra(EXTRA_TAB)?.let(::showTab)
        handleIntent(intent)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString(STATE_TAB, tabName)
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        scope.launch {
            var engine = SyncEngine.current
            while (engine == null) {
                delay(100)
                engine = SyncEngine.current
            }
            engine.onUiResumed()
        }
        startTab()
    }

    override fun onPause() {
        resumed = false
        SyncEngine.current?.uiInForeground = false
        stopTab()
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // We have focus, so reading is allowed: pick up anything copied while we were away.
        if (hasFocus) SyncEngine.current?.let { e -> runCatching { ClipboardBridge(this).read() }.getOrNull()?.let { e.onLocalClipboard(it) } }
    }

    override fun onDestroy() {
        stopTab()
        scope.cancel()
        super.onDestroy()
    }

    // ---------------------------------------------------------------------------------------------
    // Frame and tabs

    private fun buildFrame(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(ui.surface)
        }
        content = FrameLayout(this)
        root.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        nav = BottomNavigationView(this).apply {
            labelVisibilityMode = NavigationBarView.LABEL_VISIBILITY_LABELED
            setBackgroundColor(ui.container)
            itemActiveIndicatorColor = android.content.res.ColorStateList.valueOf(ui.primaryContainer)
            TABS.forEachIndexed { i, (_, label, icon) -> menu.add(0, i, i, label).setIcon(icon) }
            setOnItemSelectedListener { item ->
                showTab(TABS[item.itemId].first)
                true
            }
        }
        root.addView(nav, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        // Android 15+ draws edge to edge: keep content clear of the status bar (the nav bar pads itself).
        root.setOnApplyWindowInsetsListener { v, insets ->
            val top = if (Build.VERSION.SDK_INT >= 30) {
                insets.getInsets(WindowInsets.Type.systemBars()).top
            } else {
                @Suppress("DEPRECATION")
                insets.systemWindowInsetTop
            }
            v.setPadding(0, top, 0, 0)
            insets
        }
        return root
    }

    private fun renderNav() {
        val index = TABS.indexOfFirst { it.first == tabName }
        if (index >= 0 && nav.selectedItemId != index) nav.selectedItemId = index
    }

    fun showTab(name: String) {
        if (TABS.none { it.first == name }) return
        if (name == tabName && tab != null) return
        stopTab()
        tabName = name
        tab = when (name) {
            TAB_FILES -> FilesTab(this)
            TAB_SETTINGS -> SettingsTab(this)
            TAB_DEVELOPER -> DeveloperTab(this)
            else -> HomeTab(this)
        }
        content.removeAllViews()
        content.addView(tab!!.build(), FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        renderNav()
        if (resumed) startTab()
    }

    private fun startTab() {
        val t = tab ?: return
        if (tabJob != null) return
        val job = Job(scope.coroutineContext[Job])
        tabJob = job
        t.start(CoroutineScope(scope.coroutineContext + job))
    }

    private fun stopTab() {
        tabJob?.cancel()
        tabJob = null
        tab?.stop()
    }

    // ---------------------------------------------------------------------------------------------
    // Actions used by the tabs

    fun sendClipboardNow() {
        val engine = SyncEngine.current
        val content = runCatching { ClipboardBridge(this).read() }
            .onFailure { EventLog.error("Reading the clipboard failed", it) }
            .getOrNull()
        when {
            engine == null -> toast("Midfielder is still starting, try again")
            content == null -> toast("The clipboard is empty, or Android didn't let Midfielder read it").also {
                EventLog.add("Manual send: clipboard empty or unreadable")
            }
            else -> toast(engine.onLocalClipboard(content, force = true))
        }
    }

    fun pickFiles() {
        @Suppress("DEPRECATION")
        startActivityForResult(
            Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
                .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true),
            REQ_PICK_FILES,
        )
    }

    fun startScan() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
            return
        }
        @Suppress("DEPRECATION")
        startActivityForResult(Intent(this, ScanActivity::class.java), REQ_SCAN)
    }

    fun confirm(title: String, message: String, positive: String, action: () -> Unit) {
        MaterialAlertDialogBuilder(this).setTitle(title).setMessage(message)
            .setPositiveButton(positive) { _, _ -> action() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    /** Deletes received files from MediaStore. Files we no longer own (e.g. after a reinstall) need the user's OK. */
    fun deleteReceived(records: List<TransferRecord>) {
        val store = HistoryStore.get(this)
        val done = ArrayList<String>()
        val needConsent = ArrayList<Pair<String, Uri>>()
        var recoverable: RecoverableSecurityException? = null
        for (r in records) {
            val uri = FileActions.uriOf(r) ?: continue
            try {
                contentResolver.delete(uri, null, null)
                done += r.id
            } catch (e: SecurityException) {
                needConsent += r.id to uri
                if (e is RecoverableSecurityException) recoverable = e
            }
        }
        store.remove(done)
        if (needConsent.isEmpty()) {
            toast(if (done.size == 1) "Deleted" else "Deleted ${done.size} files")
            return
        }
        pendingDelete.clear()
        pendingDelete += needConsent.map { it.first }
        val sender: IntentSender? = if (Build.VERSION.SDK_INT >= 30) {
            MediaStore.createDeleteRequest(contentResolver, needConsent.map { it.second }).intentSender
        } else {
            recoverable?.userAction?.actionIntent?.intentSender
        }
        if (sender == null) return toast("Android didn't allow deleting this file")
        runCatching {
            @Suppress("DEPRECATION")
            startIntentSenderForResult(sender, REQ_DELETE, null, 0, 0, 0)
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Pairing and intents

    private fun handleIntent(intent: Intent?) {
        intent ?: return
        val data = intent.data
        if (data != null && data.scheme == "midfielder" && data.host == "pair") confirmPairing(data.toString())
        intent.getStringExtra(EXTRA_OPEN_RECORD)?.let { id ->
            intent.removeExtra(EXTRA_OPEN_RECORD)
            HistoryStore.get(this).records().firstOrNull { it.id == id }?.let { FileActions.open(this, it) }
        }
    }

    private fun confirmPairing(uri: String) {
        val invite = runCatching { PairingInvite.parse(uri) }.getOrElse {
            toast("That isn't a Midfielder pairing code")
            return
        }
        MaterialAlertDialogBuilder(this)
            .setTitle("Pair with ${invite.name}?")
            .setMessage("Check that your PC shows this fingerprint:\n\n${invite.fingerprint.display()}")
            .setPositiveButton("Pair") { _, _ -> pair(invite) }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun pair(invite: PairingInvite) {
        scope.launch {
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
        if (requestCode == REQ_DELETE) {
            if (resultCode == RESULT_OK) {
                HistoryStore.get(this).remove(pendingDelete.toList())
                toast("Deleted")
            }
            pendingDelete.clear()
            return
        }
        if (resultCode != RESULT_OK || data == null) return
        when (requestCode) {
            REQ_SCAN -> data.getStringExtra(ScanActivity.EXTRA_URI)?.let(::confirmPairing)
            REQ_PICK_FILES -> {
                val clip = data.clipData
                val uris = if (clip != null) (0 until clip.itemCount).map { clip.getItemAt(it).uri } else listOfNotNull(data.data)
                SyncService.sendUris(this, uris)
                toast(if (uris.size == 1) "Sending file" else "Sending ${uris.size} files")
            }
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_CAMERA && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScan()
        (tab as? SettingsTab)?.renderChecklist()
        if (requestCode == REQ_FEATURES) (tab as? SettingsTab)?.onPermissionsResult()
        (tab as? HomeTab)?.refreshSetup()
    }

    private fun toast(s: String) = snack(s)

    /** Short message above the navigation bar, with an optional action (e.g. Undo). */
    fun snack(text: String, action: String? = null, onAction: (() -> Unit)? = null) {
        val bar = Snackbar.make(content, text, Snackbar.LENGTH_LONG).setAnchorView(nav)
        if (action != null && onAction != null) bar.setAction(action) { onAction() }
        bar.show()
    }

    companion object {
        const val EXTRA_TAB = "tab"
        const val EXTRA_OPEN_RECORD = "open_record"
        /** Debug builds only: fill history with sample files (for screenshots). */
        const val EXTRA_DEMO = "demo"
        const val TAB_HOME = "home"
        const val TAB_FILES = "files"
        const val TAB_SETTINGS = "settings"
        const val TAB_DEVELOPER = "developer"

        private val TABS = listOf(
            Triple(TAB_HOME, "Home", R.drawable.ic_home),
            Triple(TAB_FILES, "Files", R.drawable.ic_folder),
            Triple(TAB_SETTINGS, "Settings", R.drawable.ic_settings),
            Triple(TAB_DEVELOPER, "Developer", R.drawable.ic_code),
        )
        private const val STATE_TAB = "tab"
        const val REQ_FEATURES = 20
        private const val REQ_SCAN = 1
        private const val REQ_PICK_FILES = 2
        private const val REQ_CAMERA = 3
        private const val REQ_DELETE = 5
    }
}
