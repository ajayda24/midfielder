using Google.Protobuf;
using Midfielder.Core.Wire;
using Midfielder.Protocol;

namespace Midfielder.Core.Feature;

/// <summary>Framing for an audio stream's data connection: one <see cref="AudioChunk"/> (20 ms of PCM16) per frame.</summary>
public static class AudioFrames
{
    public const int ChunkMs = 20;
    private const int MaxFrameBytes = 64 * 1024;

    /// <summary>Bytes of interleaved PCM16 in one chunk.</summary>
    public static int ChunkBytes(int sampleRate, int channels) => sampleRate / 1000 * ChunkMs * channels * 2;

    public static async ValueTask WriteAsync(Stream stream, uint seq, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        await Envelopes.WriteAsync(stream, new Envelope { AudioChunk = new AudioChunk { Seq = seq, Pcm = ByteString.CopyFrom(pcm.Span) } }, MaxFrameBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next chunk, or null when the stream ended. Other frames are skipped.</summary>
    public static async ValueTask<AudioChunk?> ReadAsync(Stream stream, CancellationToken ct)
    {
        while (true)
        {
            var env = await Envelopes.ReadAsync(stream, MaxFrameBytes, ct).ConfigureAwait(false);
            if (env is null) return null;
            if (env.BodyCase == Envelope.BodyOneofCase.AudioChunk) return env.AudioChunk;
        }
    }
}
