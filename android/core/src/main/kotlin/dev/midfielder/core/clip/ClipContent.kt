package dev.midfielder.core.clip

import dev.midfielder.core.crypto.Hashes
import dev.midfielder.core.crypto.Hex

/** Clipboard content in a platform-neutral form. */
public sealed class ClipContent {
    public abstract val sensitive: Boolean
    public abstract val mime: String

    /** Canonical bytes: UTF-8 text or PNG. */
    public abstract val bytes: ByteArray

    public val sha256: ByteArray by lazy { Hashes.sha256(bytes) }
    public val sha256Hex: String get() = Hex.encode(sha256)

    public class Text(public val text: String, override val sensitive: Boolean = false) : ClipContent() {
        override val mime: String get() = "text/plain"
        override val bytes: ByteArray by lazy { text.toByteArray(Charsets.UTF_8) }
    }

    public class Image(public val png: ByteArray, override val sensitive: Boolean = false) : ClipContent() {
        override val mime: String get() = "image/png"
        override val bytes: ByteArray get() = png
    }
}
