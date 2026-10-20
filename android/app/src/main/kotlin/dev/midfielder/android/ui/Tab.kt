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

    protected suspend fun engine(): SyncEngine {
        while (true) {
            SyncEngine.current?.let { return it }
            delay(100)
        }
    }
}
