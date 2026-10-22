package dev.midfielder.android

import android.Manifest
import android.app.RecoverableSecurityException
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.IntentSender
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.FrameLayout
import android.widget.LinearLayout
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AlertDialog
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
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The app's only regular screen: three tabs (Home, Files, Settings) over a bottom bar, plus Developer, opened from
 * Settings. Framework views only. Each tab's view is built once and kept while the activity lives, so switching
 * tabs keeps scroll position and selection; a tab's coroutines run only while it's shown and the activity is
 * resumed.
 */
class MainActivity : AppCompatActivity() {
    lateinit var ui: Ui
        private set

    private val scope = MainScope()
    private var tabJob: Job? = null
    private var resumeJob: Job? = null
    private val tabs = HashMap<String, Pair<Tab, View>>()
    private var tab: Tab? = null
    private var tabName = ""
    private var resumed = false
    private lateinit var content: FrameLayout
    private lateinit var nav: NavigationBarView
    private val pendingDelete = ArrayList<String>()
    private var pendingInvite: String? = null
    private var pairing: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ui = Ui(this)
        SyncService.start(this)
        setContentView(buildFrame())
        showTab(savedInstanceState?.getString(STATE_TAB) ?: intent.getStringExtra(EXTRA_TAB) ?: TAB_HOME)
        // Only a fresh launch acts on its intent: after rotation, a theme change or a restore from Recents the same
        // intent comes back, and must not reopen a file or ask to pair again.
        val fromHistory = intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY != 0
        if (savedInstanceState == null && !fromHistory) handleIntent(intent)
        savedInstanceState?.getString(STATE_INVITE)?.let(::confirmPairing) // the question survives rotation
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    tab?.onBack() == true -> Unit
                    tabName == TAB_DEVELOPER -> showTab(TAB_SETTINGS)
                    tabName != TAB_HOME -> showTab(TAB_HOME)
                    else -> {
                        isEnabled = false
                        onBackPressedDispatcher.onBackPressed()
                        isEnabled = true
                    }
                }
            }
        })
        if (savedInstanceState == null && intent.getBooleanExtra(EXTRA_DEMO, false) && isDebuggable()) Demo.seed(this)
        demoFeatures(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        demoFeatures(intent)
        intent.getStringExtra(EXTRA_TAB)?.let(::showTab)
        handleIntent(intent)
    }

    private fun isDebuggable() = applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0

    /** Debug builds only: turn every feature on for screenshots (`--ez demo_features true`). */
    private fun demoFeatures(intent: Intent) {
        if (!intent.getBooleanExtra(EXTRA_DEMO_FEATURES, false) || !isDebuggable()) return
        Demo.allFeatures(this)
        // Rebuild every tab so the new switches show.
        val current = tabName.ifEmpty { TAB_HOME }
        stopTab()
        tabs.clear()
        tab = null
        tabName = ""
        content.removeAllViews()
        showTab(current)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString(STATE_TAB, tabName)
        pendingInvite?.let { outState.putString(STATE_INVITE, it) }
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        resumeJob = scope.launch {
            val engine = awaitEngine() ?: return@launch
            if (!resumed) return@launch
            engine.onUiResumed()
            // Permissions granted (or revoked) in Android's settings while we were away take effect now.
            engine.features.apply()
        }
        startTab()
    }

    override fun onPause() {
        resumed = false
        resumeJob?.cancel()
        resumeJob = null
        SyncEngine.current?.uiInForeground = false
        stopTab()
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // We have focus, so reading is allowed: pick up anything copied while we were away (only if it changed,
        // and only when clipboard sync is on; an image is read off the main thread).
        if (!hasFocus) return
        val engine = SyncEngine.current ?: return
        if (!engine.clipSync.enabled) return
        runCatching { ClipboardBridge(this).grab() }.getOrNull()?.let { engine.onClipboardGrab(it, force = false) }
    }

    override fun onDestroy() {
        stopTab()
        pairing?.dismiss()
        tabs.values.forEach { (t, _) -> t.destroy() }
        scope.cancel()
        super.onDestroy()
    }

    /** The engine, once the service has built it (normally instant); null if it doesn't come up in time. */
    private suspend fun awaitEngine(): SyncEngine? = withTimeoutOrNull(ENGINE_WAIT_MS) {
        var engine = SyncEngine.current
        while (engine == null) {
            delay(100)
            engine = SyncEngine.current
        }
        engine
    }

    // ---------------------------------------------------------------------------------------------
    // Frame and tabs

    private fun buildFrame(): View {
        // A phone on its side has little height: a rail on the left instead of a bar that takes a third of it.
        val cfg = resources.configuration
        val rail = cfg.screenWidthDp > cfg.screenHeightDp && cfg.screenHeightDp < 600
        val root = LinearLayout(this).apply {
            orientation = if (rail) LinearLayout.HORIZONTAL else LinearLayout.VERTICAL
            setBackgroundColor(ui.surface)
        }
        content = FrameLayout(this)
        nav = (if (rail) com.google.android.material.navigationrail.NavigationRailView(this).apply {
            fitsSystemWindows = false // the root already keeps clear of the bars
            menuGravity = android.view.Gravity.CENTER
        } else BottomNavigationView(this)).apply {
            labelVisibilityMode = NavigationBarView.LABEL_VISIBILITY_LABELED
            setBackgroundColor(ui.container)
            itemActiveIndicatorColor = android.content.res.ColorStateList.valueOf(ui.primaryContainer)
            NAV.forEachIndexed { i, (_, label, icon) -> menu.add(0, i, i, label).setIcon(icon) }
            setOnItemSelectedListener { item ->
                showTab(NAV[item.itemId].first)
                true
            }
            // Tapping the tab you're on goes back to its top.
            setOnItemReselectedListener { item ->
                val name = NAV[item.itemId].first
                if (name == TAB_SETTINGS && tabName == TAB_DEVELOPER) showTab(TAB_SETTINGS) else tab?.scrollToTop()
            }
        }
        if (rail) {
            root.addView(nav, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT))
            root.addView(content, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        } else {
            root.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
            root.addView(nav, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        }
        // Android 15+ draws edge to edge: keep content clear of the status bar, and of side bars and cutouts in
        // landscape (the bottom bar pads itself).
        root.setOnApplyWindowInsetsListener { v, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                v.setPadding(bars.left, bars.top, bars.right, 0)
            } else {
                @Suppress("DEPRECATION")
                v.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, 0)
            }
            insets
        }
        return root
    }

    private fun renderNav() {
        val shown = if (tabName == TAB_DEVELOPER) TAB_SETTINGS else tabName
        val index = NAV.indexOfFirst { it.first == shown }
        if (index >= 0 && nav.selectedItemId != index) nav.selectedItemId = index
    }

    fun showTab(name: String) {
        if (name !in ALL_TABS) return
        if (name == tabName && tab != null) return
        stopTab()
        tabs[tabName]?.second?.visibility = View.GONE
        tabName = name
        val (t, view) = tabs.getOrPut(name) {
            val created = when (name) {
                TAB_FILES -> FilesTab(this)
                TAB_SETTINGS -> SettingsTab(this)
                TAB_DEVELOPER -> DeveloperTab(this)
                else -> HomeTab(this)
            }
            val v = created.build()
            created.view = v
            content.addView(v, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            created to v
        }
        tab = t
        view.visibility = View.VISIBLE
        renderNav()
        if (resumed) startTab()
    }

    /** Opens Settings scrolled to the automatic-copy checklist (from Home's "Finish setup" banner). */
    fun showSetup() {
        showTab(TAB_SETTINGS)
        (tab as? SettingsTab)?.scrollToSetup()
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
        val engine = SyncEngine.current ?: return snack("Midfielder is still starting. Try again in a moment.")
        if (noPcPaired(engine)) return
        val grab = runCatching { ClipboardBridge(this).grab(force = true) }
            .onFailure { EventLog.error("Reading the clipboard failed", it) }
            .getOrNull()
        if (grab == null) {
            EventLog.add("Manual send: clipboard empty or unreadable")
            return snack("The clipboard is empty")
        }
        engine.onClipboardGrab(grab, force = true) { snack(it) }
    }

    fun pickFiles() {
        val engine = SyncEngine.current
        if (engine != null && noPcPaired(engine)) return
        val open = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
        try {
            @Suppress("DEPRECATION")
            startActivityForResult(open, REQ_PICK_FILES)
        } catch (_: ActivityNotFoundException) {
            // Some managed or Go devices have no document picker.
            runCatching {
                @Suppress("DEPRECATION")
                startActivityForResult(
                    Intent(Intent.ACTION_GET_CONTENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true),
                    REQ_PICK_FILES,
                )
            }.onFailure { snack("This phone has no file picker") }
        }
    }

    /** With nothing paired there's nowhere to send: say so, with a way to pair. */
    private fun noPcPaired(engine: SyncEngine): Boolean {
        if (engine.status.value.paired.isNotEmpty()) return false
        snack("Pair with your PC first", "Pair") { startScan() }
        return true
    }

    fun startScan() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            if (Permissions.blocked(this, Manifest.permission.CAMERA)) {
                snack("Allow the camera for Midfielder to scan the code", "Settings") { Permissions.openAppSettings(this) }
            } else {
                Permissions.request(this, arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
            }
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

    /**
     * Deletes received files from MediaStore, off the main thread (many files = many calls). Files we no longer own
     * (e.g. after a reinstall) need the user's OK; only what was really deleted leaves the history.
     */
    fun deleteReceived(records: List<TransferRecord>) {
        scope.launch {
            val store = HistoryStore.get(this@MainActivity)
            val done = ArrayList<String>()
            val needConsent = ArrayList<Pair<String, Uri>>()
            var recoverable: RecoverableSecurityException? = null
            withContext(Dispatchers.IO) {
                for (r in records) {
                    val uri = FileActions.uriOf(r) ?: continue
                    try {
                        contentResolver.delete(uri, null, null)
                        done += r.id
                    } catch (e: SecurityException) {
                        // Android 10 can only ask about one file at a time: ask for the first, keep the rest.
                        if (Build.VERSION.SDK_INT < 30 && needConsent.isNotEmpty()) continue
                        needConsent += r.id to uri
                        if (e is RecoverableSecurityException) recoverable = e
                    }
                }
            }
            store.remove(done)
            if (needConsent.isEmpty()) {
                snack(if (done.size == 1) "Deleted" else "Deleted ${done.size} files")
                return@launch
            }
            pendingDelete.clear()
            pendingDelete += needConsent.map { it.first }
            val sender: IntentSender? = if (Build.VERSION.SDK_INT >= 30) {
                MediaStore.createDeleteRequest(contentResolver, needConsent.map { it.second }).intentSender
            } else {
                recoverable?.userAction?.actionIntent?.intentSender
            }
            if (sender == null) return@launch snack("Android didn't allow deleting this file")
            runCatching {
                @Suppress("DEPRECATION")
                startIntentSenderForResult(sender, REQ_DELETE, null, 0, 0, 0)
            }
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
            snack("That isn't a Midfielder pairing code")
            return
        }
        // A pairing code always points at a PC on your own network; anything else is someone else's PC.
        if (!PairLinks.isLocal(invite)) {
            snack("That code points outside your network, so Midfielder won't pair with it")
            EventLog.add("Refused a pairing code for a non-local address")
            return
        }
        pendingInvite = uri
        MaterialAlertDialogBuilder(this)
            .setTitle("Pair with ${invite.name}?")
            .setMessage(
                "Only pair with your own PC: it will get everything you copy and the files you send.\n\n" +
                    "Check that your PC shows this fingerprint:\n\n${invite.fingerprint.display()}",
            )
            .setPositiveButton("Pair") { _, _ ->
                pendingInvite = null
                pair(invite)
            }
            .setNegativeButton("Cancel") { _, _ -> pendingInvite = null }
            .setOnCancelListener { pendingInvite = null }
            .show()
    }

    /** Pairing runs in the engine, so rotating or leaving the screen doesn't cancel it. */
    private fun pair(invite: PairingInvite) {
        pairing = MaterialAlertDialogBuilder(this)
            .setTitle("Pairing with ${invite.name}…")
            .setMessage("Keep Midfielder open on your PC.")
            .setCancelable(false)
            .show()
        scope.launch {
            val engine = awaitEngine()
            if (engine == null) {
                pairing?.dismiss()
                return@launch snack("Midfielder didn't start. Close it and open it again.")
            }
            engine.pairInBackground(invite) { result ->
                pairing?.dismiss()
                pairing = null
                result.onSuccess { snack("Paired with ${it.name}") }
                    .onFailure { snack(PairLinks.describeFailure(invite, it)) }
            }
        }
    }

    @Deprecated("Framework API; this app has no AndroidX dependency")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ_DELETE) {
            if (resultCode == RESULT_OK) {
                HistoryStore.get(this).remove(pendingDelete.toList())
                snack("Deleted")
            }
            pendingDelete.clear()
            return
        }
        if (requestCode == REQ_SCAN && resultCode != RESULT_OK) {
            data?.getStringExtra(ScanActivity.EXTRA_ERROR)?.let { snack(it) }
            return
        }
        if (resultCode != RESULT_OK || data == null) return
        when (requestCode) {
            REQ_SCAN -> data.getStringExtra(ScanActivity.EXTRA_URI)?.let(::confirmPairing)
            REQ_PICK_FILES -> {
                val clip = data.clipData
                val uris = if (clip != null) (0 until clip.itemCount).map { clip.getItemAt(it).uri } else listOfNotNull(data.data)
                // Keep access, so sent files can still be opened or shared from Files later.
                for (u in uris) runCatching { contentResolver.takePersistableUriPermission(u, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
                SyncService.sendUris(this, uris)
                snack(sendingText(uris.size))
            }
        }
    }

    /** "Sending" only when a PC is there to receive; otherwise when it will go. */
    private fun sendingText(count: Int): String {
        val what = if (count == 1) "file" else "$count files"
        val status = SyncEngine.current?.status?.value ?: return "Sending $what"
        return when {
            status.connected.isNotEmpty() -> "Sending $what to ${status.connected.first()}"
            status.paired.isNotEmpty() -> "The $what will go when ${status.paired.first()} connects"
            else -> "Pair with your PC first"
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_CAMERA) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScan()
            else snack("Camera access is needed to scan. You can also scan the code with any camera app.")
        }
        val settings = tabs[TAB_SETTINGS]?.first as? SettingsTab
        settings?.renderChecklist()
        if (requestCode == REQ_FEATURES) settings?.onPermissionsResult()
        (tabs[TAB_HOME]?.first as? HomeTab)?.refreshSetup()
    }

    /** Short message above the navigation bar, with an optional action (e.g. Undo). All feedback goes here. */
    fun snack(text: String, action: String? = null, onAction: (() -> Unit)? = null) {
        if (isFinishing || isDestroyed) return
        val bar = Snackbar.make(content, text, Snackbar.LENGTH_LONG)
        if (nav is BottomNavigationView) bar.setAnchorView(nav)
        if (action != null && onAction != null) bar.setAction(action) { onAction() }
        bar.show()
    }

    companion object {
        const val EXTRA_TAB = "tab"
        const val EXTRA_OPEN_RECORD = "open_record"
        /** Debug builds only: fill history with sample files (for screenshots). */
        const val EXTRA_DEMO = "demo"
        const val EXTRA_DEMO_FEATURES = "demo_features"
        const val TAB_HOME = "home"
        const val TAB_FILES = "files"
        const val TAB_SETTINGS = "settings"
        const val TAB_DEVELOPER = "developer"

        /** The bottom bar; Developer is reached from Settings. */
        private val NAV = listOf(
            Triple(TAB_HOME, "Home", R.drawable.ic_home),
            Triple(TAB_FILES, "Files", R.drawable.ic_folder),
            Triple(TAB_SETTINGS, "Settings", R.drawable.ic_settings),
        )
        private val ALL_TABS = setOf(TAB_HOME, TAB_FILES, TAB_SETTINGS, TAB_DEVELOPER)
        private const val STATE_TAB = "tab"
        private const val STATE_INVITE = "invite"
        private const val ENGINE_WAIT_MS = 10_000L
        const val REQ_FEATURES = 20
        private const val REQ_SCAN = 1
        private const val REQ_PICK_FILES = 2
        private const val REQ_CAMERA = 3
        private const val REQ_DELETE = 5
    }
}
