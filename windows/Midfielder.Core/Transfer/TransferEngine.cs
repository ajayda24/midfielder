using System.Buffers;
using System.Security.Cryptography;
using Google.Protobuf;
using Midfielder.Core.Wire;
using Midfielder.Protocol;

namespace Midfielder.Core.Transfer;

public sealed class TransferFailedException : IOException
{
    public TransferFailedException() { }
    public TransferFailedException(string message) : base(message) { }
    public TransferFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Streams one file over an open data connection (identical protocol to the Kotlin TransferEngine):
/// receiver → DataResume(offset); sender → raw bytes [offset, size) then DataDone(sha256);
/// receiver → DataDone(ok). Memory use is one pooled chunk buffer, whatever the file size.
/// A read or write that makes no progress for <c>stallTimeoutMs</c> fails the attempt.
/// </summary>
public static class TransferEngine
{
    public const int DefaultChunkBytes = 64 * 1024;
    private const long ProgressStepBytes = 1024 * 1024;

    public delegate void Progress(long transferred, long total);

    public static async Task SendAsync(Stream net, IFileSource source, FileEntry entry, Progress? progress, int stallTimeoutMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallTimeoutMs);
        var resume = (await Envelopes.RequireAsync(net, Limits.MaxHandshakeFrameBytes, "DataResume", stall.Token).ConfigureAwait(false)).DataResume
            ?? throw new ProtocolException("expected DataResume");
        long size = checked((long)entry.Size);
        if (resume.Offset > (ulong)size) throw new ProtocolException($"resume offset {resume.Offset} outside 0..{size}");
        long offset = (long)resume.Offset;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buf = ArrayPool<byte>.Shared.Rent(DefaultChunkBytes);
        try
        {
            var src = source.Open();
            await using (src.ConfigureAwait(false))
            {
                await CopyExactlyAsync(src, null, offset, hash, buf, null, stall, stallTimeoutMs).ConfigureAwait(false);
                await CopyExactlyAsync(src, net, size - offset, hash, buf, done => progress?.Invoke(checked(offset + done), size), stall, stallTimeoutMs).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        stall.CancelAfter(stallTimeoutMs);
        await Envelopes.WriteAsync(net, new Envelope { DataDone = new DataDone { Ok = true, Sha256 = ByteString.CopyFrom(hash.GetHashAndReset()) } }, Limits.MaxHandshakeFrameBytes, stall.Token).ConfigureAwait(false);
        await net.FlushAsync(stall.Token).ConfigureAwait(false);
        var verdict = (await Envelopes.RequireAsync(net, Limits.MaxHandshakeFrameBytes, "DataDone", stall.Token).ConfigureAwait(false)).DataDone
            ?? throw new ProtocolException("expected DataDone");
        if (!verdict.Ok) throw new TransferFailedException($"receiver rejected file: {Truncate(verdict.Reason, 200)}");
    }

    /// <summary>Returns the committed path.</summary>
    public static async Task<string> ReceiveAsync(Stream net, IFileSink sink, FileEntry entry, Progress? progress, int stallTimeoutMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(entry);
        long size = checked((long)entry.Size);
        long offset = sink.PartialLength();
        if (offset < 0 || offset > size)
        {
            sink.Discard();
            offset = 0;
        }
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallTimeoutMs);
        await Envelopes.WriteAsync(net, new Envelope { DataResume = new DataResume { Offset = (ulong)offset } }, Limits.MaxHandshakeFrameBytes, stall.Token).ConfigureAwait(false);
        await net.FlushAsync(stall.Token).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buf = ArrayPool<byte>.Shared.Rent(DefaultChunkBytes);
        try
        {
            if (offset > 0)
            {
                var partial = sink.OpenPartial();
                await using (partial.ConfigureAwait(false))
                {
                    await CopyExactlyAsync(partial, null, offset, hash, buf, null, stall, stallTimeoutMs).ConfigureAwait(false);
                }
            }
            var output = sink.OpenAt(offset);
            await using (output.ConfigureAwait(false))
            {
                await CopyExactlyAsync(net, output, size - offset, hash, buf, done => progress?.Invoke(checked(offset + done), size), stall, stallTimeoutMs).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        stall.CancelAfter(stallTimeoutMs);
        var trailer = (await Envelopes.RequireAsync(net, Limits.MaxHandshakeFrameBytes, "DataDone", stall.Token).ConfigureAwait(false)).DataDone
            ?? throw new ProtocolException("expected DataDone");
        byte[] actual = hash.GetHashAndReset();
        bool matchesTrailer = trailer.Ok && CryptographicOperations.FixedTimeEquals(actual, trailer.Sha256.Span);
        bool matchesOffer = entry.Sha256.IsEmpty || CryptographicOperations.FixedTimeEquals(actual, entry.Sha256.Span);
        if (!matchesTrailer || !matchesOffer)
        {
            sink.Discard();
            await ReplyAsync(net, false, "hash mismatch", stall.Token).ConfigureAwait(false);
            throw new TransferFailedException($"hash mismatch for {entry.Name}");
        }
        string location = sink.Commit();
        await ReplyAsync(net, true, "", stall.Token).ConfigureAwait(false);
        return location;
    }

    private static async Task ReplyAsync(Stream net, bool ok, string reason, CancellationToken ct)
    {
        await Envelopes.WriteAsync(net, new Envelope { DataDone = new DataDone { Ok = ok, Reason = reason } }, Limits.MaxHandshakeFrameBytes, ct).ConfigureAwait(false);
        await net.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task CopyExactlyAsync(Stream from, Stream? to, long count, IncrementalHash hash, byte[] buf, Action<long>? onProgress, CancellationTokenSource stall, int stallTimeoutMs)
    {
        long remaining = count;
        long done = 0;
        long lastReported = 0;
        int chunk = Math.Min(buf.Length, DefaultChunkBytes);
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, chunk);
            stall.CancelAfter(stallTimeoutMs);
            int r = await from.ReadAsync(buf.AsMemory(0, want), stall.Token).ConfigureAwait(false);
            if (r == 0) throw new EndOfStreamException($"stream ended with {remaining} bytes left");
            hash.AppendData(buf, 0, r);
            if (to is not null) await to.WriteAsync(buf.AsMemory(0, r), stall.Token).ConfigureAwait(false);
            remaining -= r;
            done += r;
            if (done - lastReported >= ProgressStepBytes || remaining == 0)
            {
                lastReported = done;
                onProgress?.Invoke(done);
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
