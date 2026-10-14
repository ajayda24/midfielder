package dev.midfielder.core.clip

import dev.midfielder.core.Limits
import dev.midfielder.core.crypto.Hashes
import dev.midfielder.proto.ClipKind
import dev.midfielder.proto.ClipboardItem
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import okio.ByteString.Companion.toByteString
import java.security.SecureRandom

/**
 * Platform-neutral clipboard sync logic.
 *
 * - Local changes are conflated: during a burst of copies only the latest is sent.
 * - Echo prevention: content we just applied from a peer is not sent back when the OS reports the change.
 * - Catch-up: on (re)connect the latest item is re-sent with catch_up = true; the receiver applies it only
 *   if it is newer than what it already has. A phone that slept through a copy gets it on wake.
 *
 * Platforms must also suppress the clipboard-change event caused by their own write when the OS may
 * re-encode content (Windows images); hashes alone catch the text case.
 */
public class ClipSync(
    private val localDeviceId: String,
    scope: CoroutineScope,
    private val broadcast: suspend (ClipboardItem) -> Unit,
    private val apply: suspend (ClipContent, ClipboardItem) -> Unit,
    private val clock: () -> Long = System::currentTimeMillis,
    public val history: ClipboardHistory = ClipboardHistory(),
) {
    private val random = SecureRandom()
    private val outgoing = Channel<ClipboardItem>(Channel.CONFLATED)
    private val lock = Any()

    // Guarded by lock.
    private var latest: ClipboardItem? = null
    private var lastAppliedHash: ByteArray? = null

    @Volatile
    public var enabled: Boolean = true

    init {
        scope.launch {
            for (item in outgoing) {
                runCatching { broadcast(item) }
            }
        }
    }

    /** Call from the platform clipboard monitor. Returns true if the content will be sent. */
    public fun onLocalChange(content: ClipContent): Boolean {
        if (!enabled) return false
        if (content is ClipContent.Text && content.bytes.size > Limits.MAX_CLIP_TEXT_BYTES) return false
        if (content.bytes.isEmpty()) return false
        // Larger images go through a TRANSFER_PURPOSE_CLIPBOARD file transfer instead (see isInlineSize).
        if (!isInlineSize(content)) return false
        val hash = content.sha256
        val item: ClipboardItem
        synchronized(lock) {
            val applied = lastAppliedHash
            if (applied != null && Hashes.constantTimeEquals(applied, hash)) return false // our own echo
            val current = latest
            if (current != null && Hashes.constantTimeEquals(current.content_sha256.toByteArray(), hash)) return false
            item = toItem(content)
            latest = item
        }
        history.add(ClipboardHistory.Entry(content, localDeviceId, item.created_at_ms))
        outgoing.trySend(item)
        return true
    }

    /** Call when a peer sends a ClipboardItem. Returns true if it was applied locally. */
    public suspend fun onRemote(item: ClipboardItem): Boolean {
        if (!enabled) return false
        val content = fromItem(item) ?: return false
        val hash = content.sha256
        if (!Hashes.constantTimeEquals(hash, item.content_sha256.toByteArray())) return false
        synchronized(lock) {
            val current = latest
            if (current != null) {
                if (Hashes.constantTimeEquals(current.content_sha256.toByteArray(), hash)) return false
                if (item.catch_up && item.created_at_ms <= current.created_at_ms) return false
            }
            latest = item.copy(catch_up = false)
            lastAppliedHash = hash
        }
        history.add(ClipboardHistory.Entry(content, item.origin_device_id, item.created_at_ms))
        apply(content, item)
        return true
    }

    /** Whether [content] fits in a ClipboardItem; larger images must be sent as a clipboard transfer. */
    public fun isInlineSize(content: ClipContent): Boolean = content.bytes.size <= Limits.MAX_INLINE_CLIP_BYTES

    /** The item to send to a peer that just connected, if any. */
    public fun catchUpItem(): ClipboardItem? = synchronized(lock) { latest?.copy(catch_up = true) }

    public fun close() {
        outgoing.close()
    }

    private fun toItem(content: ClipContent): ClipboardItem {
        val id = ByteArray(16).also { random.nextBytes(it) }
        return ClipboardItem(
            id = id.toByteString(),
            origin_device_id = localDeviceId,
            created_at_ms = clock(),
            kind = if (content is ClipContent.Image) ClipKind.CLIP_KIND_IMAGE else ClipKind.CLIP_KIND_TEXT,
            mime = content.mime,
            content_sha256 = content.sha256.toByteString(),
            inline_data = content.bytes.toByteString(),
            size = content.bytes.size.toLong(),
            sensitive = content.sensitive,
        )
    }

    private fun fromItem(item: ClipboardItem): ClipContent? {
        val data = item.inline_data
        if (data.size == 0 || data.size.toLong() != item.size) return null
        return when (item.kind) {
            ClipKind.CLIP_KIND_TEXT -> {
                if (data.size > Limits.MAX_CLIP_TEXT_BYTES) return null
                ClipContent.Text(data.utf8(), item.sensitive)
            }
            ClipKind.CLIP_KIND_IMAGE -> {
                if (!isPng(data.toByteArray())) return null
                ClipContent.Image(data.toByteArray(), item.sensitive)
            }
            else -> null
        }
    }

    private fun isPng(b: ByteArray): Boolean =
        b.size > 8 && b[0] == 0x89.toByte() && b[1] == 'P'.code.toByte() && b[2] == 'N'.code.toByte() &&
            b[3] == 'G'.code.toByte()
}
