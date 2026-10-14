using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Midfielder.Protocol;

namespace Midfielder.Core.Wire;

/// <summary>
/// Length-prefixed framing (4-byte big-endian length, then an encoded <see cref="Envelope"/>).
/// The length is validated before any buffer is rented, and buffers come from the shared pool.
/// </summary>
public static class Envelopes
{
    public const int HeaderBytes = 4;

    public static async ValueTask WriteAsync(Stream stream, Envelope envelope, int maxFrameBytes, CancellationToken ct)
    {
        int size = envelope.CalculateSize();
        if (size < 1 || size > maxFrameBytes) throw new ProtocolException($"refusing to send frame of {size} bytes");
        byte[] buf = ArrayPool<byte>.Shared.Rent(size + HeaderBytes);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)size);
            envelope.WriteTo(buf.AsSpan(HeaderBytes, size));
            await stream.WriteAsync(buf.AsMemory(0, size + HeaderBytes), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Returns null on a clean end-of-stream at a frame boundary.</summary>
    public static async ValueTask<Envelope?> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderBytes);
        uint length;
        try
        {
            int first = await stream.ReadAsync(header.AsMemory(0, HeaderBytes), ct).ConfigureAwait(false);
            if (first == 0) return null;
            if (first < HeaderBytes)
            {
                await stream.ReadExactlyAsync(header.AsMemory(first, HeaderBytes - first), ct).ConfigureAwait(false);
            }
            length = BinaryPrimitives.ReadUInt32BigEndian(header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        // Compared as unsigned: 0xFFFFFFFF can't wrap to a small or negative int.
        if (length < 1 || length > (uint)maxFrameBytes)
            throw new ProtocolException($"frame length {length} outside 1..{maxFrameBytes}");

        int n = (int)length;
        byte[] payload = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            await stream.ReadExactlyAsync(payload.AsMemory(0, n), ct).ConfigureAwait(false);
            return Decode(payload, n);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public static Envelope Decode(byte[] buffer, int length)
    {
        try
        {
            return Envelope.Parser.ParseFrom(buffer, 0, length);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new ProtocolException("malformed envelope", e);
        }
    }

    /// <summary>Reads the next envelope; end-of-stream is an error (used during handshakes).</summary>
    public static async ValueTask<Envelope> RequireAsync(Stream stream, int maxFrameBytes, string what, CancellationToken ct) =>
        await ReadAsync(stream, maxFrameBytes, ct).ConfigureAwait(false)
        ?? throw new ProtocolException($"connection closed while waiting for {what}");
}
