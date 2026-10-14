package dev.midfielder.core.clip

/**
 * Recent clipboard items, newest first, bounded by both count and total bytes.
 * Sensitive items (passwords, OTPs) are never stored.
 */
public class ClipboardHistory(
    private val maxItems: Int = 100,
    private val maxTotalBytes: Long = 32L * 1024 * 1024,
) {
    public class Entry(public val content: ClipContent, public val originDeviceId: String, public val atMs: Long)

    // Insertion-ordered; re-adding an existing hash moves it to the end (newest).
    private val entries = LinkedHashMap<String, Entry>()
    private var totalBytes = 0L

    init {
        require(maxItems > 0 && maxTotalBytes > 0)
    }

    @Synchronized
    public fun add(entry: Entry) {
        if (entry.content.sensitive) return
        val size = entry.content.bytes.size.toLong()
        if (size > maxTotalBytes) return
        val key = entry.content.sha256Hex
        entries.remove(key)?.let { totalBytes -= it.content.bytes.size }
        entries[key] = entry
        totalBytes += size
        val it = entries.values.iterator()
        while ((entries.size > maxItems || totalBytes > maxTotalBytes) && it.hasNext()) {
            totalBytes -= it.next().content.bytes.size
            it.remove()
        }
    }

    @Synchronized
    public fun snapshot(): List<Entry> = entries.values.reversed()

    @Synchronized
    public fun clear() {
        entries.clear()
        totalBytes = 0
    }

    @get:Synchronized
    public val size: Int get() = entries.size

    @get:Synchronized
    public val bytes: Long get() = totalBytes
}
