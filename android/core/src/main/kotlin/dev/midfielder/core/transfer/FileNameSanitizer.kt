package dev.midfielder.core.transfer

import dev.midfielder.core.Limits

/**
 * Turns a name from a peer into a safe file name on both Windows and Android:
 * no directories, no path traversal, no reserved device names, no control characters, bounded length.
 */
public object FileNameSanitizer {
    private const val FALLBACK = "file"
    private const val MAX_EXTENSION_CHARS = 16
    private val ILLEGAL = setOf('<', '>', ':', '"', '/', '\\', '|', '?', '*')
    private val RESERVED = buildSet {
        addAll(listOf("CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"))
        for (i in 0..9) {
            add("COM$i")
            add("LPT$i")
        }
    }

    public fun sanitize(raw: String): String {
        // Keep only the last path segment, whichever separator the sender used.
        val base = raw.substring(maxOf(raw.lastIndexOf('/'), raw.lastIndexOf('\\')) + 1)
        val cleaned = buildString(base.length) {
            for (c in base) {
                if (c.code < 0x20 || c.code == 0x7F || c in ILLEGAL) append('_') else append(c)
            }
        }.trim().trimEnd('.', ' ')

        var name = if (cleaned.isEmpty() || cleaned.all { it == '.' }) FALLBACK else cleaned
        val stem = name.substringBefore('.')
        if (stem.uppercase() in RESERVED) name = "_$name"
        return truncate(name)
    }

    private fun truncate(name: String): String {
        if (name.length <= Limits.MAX_FILE_NAME_CHARS) return name
        val dot = name.lastIndexOf('.')
        val ext = if (dot > 0 && name.length - dot <= MAX_EXTENSION_CHARS) name.substring(dot) else ""
        var stem = name.substring(0, name.length - ext.length).take(Limits.MAX_FILE_NAME_CHARS - ext.length)
        // Don't cut a surrogate pair in half.
        if (stem.isNotEmpty() && Character.isHighSurrogate(stem.last())) stem = stem.dropLast(1)
        return stem + ext
    }
}
