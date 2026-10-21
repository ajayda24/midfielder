package dev.midfielder.core.feature

import dev.midfielder.core.wire.Envelopes
import dev.midfielder.proto.AudioChunk
import dev.midfielder.proto.Envelope
import okio.ByteString.Companion.toByteString
import java.io.InputStream
import java.io.OutputStream

/** Framing for an audio stream's data connection: one [AudioChunk] (20 ms of PCM16) per frame. */
public object AudioFrames {
    public const val CHUNK_MS: Int = 20
    public const val CAPABILITY: String = "audio"
    private const val MAX_FRAME_BYTES = 64 * 1024

    /** Bytes of interleaved PCM16 in one chunk. */
    public fun chunkBytes(sampleRate: Int, channels: Int): Int = sampleRate / 1000 * CHUNK_MS * channels * 2

    public fun write(out: OutputStream, seq: Int, pcm: ByteArray) {
        Envelopes.write(out, Envelope(audio_chunk = AudioChunk(seq = seq, pcm = pcm.toByteString())), MAX_FRAME_BYTES)
        out.flush()
    }

    /** The next chunk, or null when the stream ended. Other frames are skipped. */
    public fun read(input: InputStream): AudioChunk? {
        while (true) {
            val env = Envelopes.read(input, MAX_FRAME_BYTES) ?: return null
            env.audio_chunk?.let { return it }
        }
    }
}
