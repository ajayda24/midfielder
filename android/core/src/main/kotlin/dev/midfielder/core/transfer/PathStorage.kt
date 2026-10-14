package dev.midfielder.core.transfer

import java.io.InputStream
import java.io.OutputStream
import java.nio.channels.Channels
import java.nio.channels.FileChannel
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption

/** [FileSource] over a local file (JVM, Windows-on-JVM tooling, tests). */
public class PathFileSource(private val path: Path, override val mime: String = "application/octet-stream") :
    FileSource {
    override val name: String = path.fileName.toString()
    override val size: Long = Files.size(path)
    override val modifiedMs: Long = Files.getLastModifiedTime(path).toMillis()
    override fun open(): InputStream = Files.newInputStream(path)
}

/** [FileSource] over bytes already in memory (small clipboard images). */
public class BytesFileSource(
    override val name: String,
    private val bytes: ByteArray,
    override val mime: String,
) : FileSource {
    override val size: Long get() = bytes.size.toLong()
    override val modifiedMs: Long = System.currentTimeMillis()
    override fun open(): InputStream = bytes.inputStream()
}

/**
 * Receives into [directory]. Partial data lives in `<directory>/.midfielder-partial/<key>.part` so a later
 * attempt with the same key resumes. [commit] moves it to a unique, sanitized name in [directory].
 */
public class DirectoryFileSink(
    private val directory: Path,
    private val key: String,
    fileName: String,
) : FileSink {
    private val finalName = FileNameSanitizer.sanitize(fileName)
    private val partial: Path = directory.resolve(PARTIAL_DIR).resolve(FileNameSanitizer.sanitize(key) + ".part")

    override fun partialLength(): Long = if (Files.exists(partial)) Files.size(partial) else 0L

    override fun openPartial(): InputStream = Files.newInputStream(partial)

    override fun openAt(offset: Long): OutputStream {
        Files.createDirectories(partial.parent)
        val ch = FileChannel.open(partial, StandardOpenOption.CREATE, StandardOpenOption.WRITE)
        try {
            ch.truncate(offset)
            ch.position(offset)
        } catch (e: Exception) {
            ch.close()
            throw e
        }
        return Channels.newOutputStream(ch)
    }

    override fun commit(): String {
        val dot = finalName.lastIndexOf('.')
        val stem = if (dot > 0) finalName.substring(0, dot) else finalName
        val ext = if (dot > 0) finalName.substring(dot) else ""
        for (i in 0 until MAX_NAME_ATTEMPTS) {
            val candidate = directory.resolve(if (i == 0) finalName else "$stem ($i)$ext")
            try {
                // No REPLACE_EXISTING: never overwrite a user's file.
                Files.move(partial, candidate)
                return candidate.toString()
            } catch (_: FileAlreadyExistsException) {
                continue
            }
        }
        throw java.io.IOException("no free file name for $finalName")
    }

    override fun discard() {
        Files.deleteIfExists(partial)
    }

    private companion object {
        const val PARTIAL_DIR = ".midfielder-partial"
        const val MAX_NAME_ATTEMPTS = 10_000
    }
}
