using Midfielder.Core.Crypto;

namespace Midfielder.Core.Discovery;

/// <summary>
/// "Please reconnect" UDP datagram the PC sends to the phone's last known address (format identical to Kotlin):
/// "MFK1" | 16-byte device id | uint16 big-endian listener port. Carries no secrets.
/// </summary>
public sealed record Knock
{
    public const int Size = 22;
    public const int DefaultPort = 47_811;
    private static readonly byte[] Magic = "MFK1"u8.ToArray();

    public Knock(string deviceId, int port)
    {
        if (!Hex.IsLowerHex(deviceId, 32)) throw new ArgumentException("device id must be 32 lowercase hex chars", nameof(deviceId));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        DeviceId = deviceId;
        Port = port;
    }

    public string DeviceId { get; }
    public int Port { get; }

    public byte[] Encode()
    {
        var b = new byte[Size];
        Magic.CopyTo(b, 0);
        Hex.Decode(DeviceId).CopyTo(b, 4);
        b[20] = (byte)(Port >> 8);
        b[21] = (byte)Port;
        return b;
    }

    public static Knock? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size || !data[..4].SequenceEqual(Magic)) return null;
        int port = (data[20] << 8) | data[21];
        return port == 0 ? null : new Knock(Hex.Encode(data[4..20]), port);
    }
}

/// <summary>Capped exponential backoff with jitter.</summary>
public sealed class Backoff
{
    private readonly long _initialMs;
    private readonly long _maxMs;
    private readonly double _factor;
    private readonly double _jitter;
    private long _current;

    public Backoff(long initialMs = 1_000, long maxMs = 5 * 60_000, double factor = 2.0, double jitter = 0.2)
    {
        if (initialMs <= 0 || maxMs < initialMs || factor < 1.0 || jitter is < 0 or > 1) throw new ArgumentException("invalid backoff");
        (_initialMs, _maxMs, _factor, _jitter, _current) = (initialMs, maxMs, factor, jitter, initialMs);
    }

    public long NextDelayMs()
    {
        long b = _current;
        _current = Math.Min(_maxMs, Math.Max(_current, (long)(_current * _factor)));
        long spread = (long)(b * _jitter);
        return spread == 0 ? b : b + Random.Shared.NextInt64(-spread, spread + 1);
    }

    public void Reset() => _current = _initialMs;
}
