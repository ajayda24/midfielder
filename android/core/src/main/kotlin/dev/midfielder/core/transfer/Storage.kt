package dev.midfielder.core.transfer

import java.io.InputStream
import java.io.OutputStream

/** Something we can send. Platforms implement this over files, content URIs, clipboard images... */
public interface FileSource {
    public val name: String
    public val size: Long
    public val mime: String
    public val modifiedMs: Long

    /** Opens the content from the beginning. The caller closes it. */
    public fun open(): InputStream
}

/**
 * Where a received file goes. Data is written to a partial location first, so an interrupted transfer
 * resumes from [partialLength] instead of starting over, and only a verified file is [commit]ted.
 */
public interface FileSink {
    /** Bytes already received by an earlier, interrupted attempt. */
    public fun partialLength(): Long

    /** Reads back the partial data (to hash it on resume). */
    public fun openPartial(): InputStream

    /** Opens for writing at [offset], discarding anything after it. */
    public fun openAt(offset: Long): OutputStream

    /** Moves the verified file to its final place and returns a human-readable location. */
    public fun commit(): String

    /** Deletes partial data. */
    public fun discard()
}
