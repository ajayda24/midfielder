package dev.midfielder.android

import android.app.Application

/**
 * Deliberately empty: nothing runs at process start beyond what Android requires. The sync engine is
 * created by [SyncService] and lives exactly as long as the service.
 */
class MidfielderApp : Application()
