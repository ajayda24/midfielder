package dev.midfielder.android.ui

import android.view.View
import dev.midfielder.android.MainActivity
import dev.midfielder.android.SyncEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay

/**
 * One screen of [MainActivity]. Its view is built when the tab is shown and dropped when another is shown;
 * [start]'s scope lives only while the tab is visible and the activity is resumed, so nothing runs otherwise.
 */
abstract class Tab(protected val host: MainActivity) {
    protected val ui: Ui get() = host.ui

    abstract fun build(): View

    open fun start(scope: CoroutineScope) {}

    open fun stop() {}

    /** Back press: return true if the tab handled it (e.g. leaving selection mode). */
    open fun onBack(): Boolean = false

    protected suspend fun engine(): SyncEngine {
        while (true) {
            SyncEngine.current?.let { return it }
            delay(100)
        }
    }

    /** Standard page container: 16 dp side margins, room at the bottom. */
    protected fun page(): android.widget.LinearLayout = ui.vertical().apply {
        setPadding(ui.dp(16), ui.dp(8), ui.dp(16), ui.dp(24))
    }
}
