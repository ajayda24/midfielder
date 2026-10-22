using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Midfielder.Core.Feature;

/// <summary>
/// The rotating token in the phone's Bluetooth LE beacon: only the paired PC (which holds the key) recognises it, and
/// it changes every 15 minutes, so nobody else can follow the phone by it.
/// </summary>
public static class PresenceToken
{
    public const int SlotSeconds = 900;
    public const int TokenBytes = 8;

    /// <summary>Manufacturer data company id (0xFFFF: reserved for testing, never a real company).</summary>
    public const ushort CompanyId = 0xFFFF;

    public const byte Version = 1;

    private static readonly byte[] Label = Encoding.ASCII.GetBytes("midfielder-presence-v1");

    public static long Slot(DateTimeOffset time) => time.ToUnixTimeSeconds() / SlotSeconds;

    public static byte[] Compute(byte[] key, long slot)
    {
        var data = new byte[Label.Length + 8];
        Label.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(Label.Length), slot);
        return HMACSHA256.HashData(key, data)[..TokenBytes];
    }

    /// <summary>True if <paramref name="token"/> is the phone's token for now or a neighbouring slot (clock skew).</summary>
    public static bool Matches(byte[] key, ReadOnlySpan<byte> token, DateTimeOffset now)
    {
        if (token.Length != TokenBytes) return false;
        long slot = Slot(now);
        for (long s = slot - 1; s <= slot + 1; s++)
        {
            if (CryptographicOperations.FixedTimeEquals(Compute(key, s), token)) return true;
        }
        return false;
    }

    /// <summary>
    /// The beacon's manufacturer data after the company id: version, then one token per PC the phone is paired with
    /// (up to two). True if one of them is this PC's token for now.
    /// </summary>
    public static bool MatchesAny(byte[] key, ReadOnlySpan<byte> manufacturerData, DateTimeOffset now)
    {
        if (manufacturerData.Length < 1 + TokenBytes || manufacturerData[0] != Version || (manufacturerData.Length - 1) % TokenBytes != 0) return false;
        for (int at = 1; at < manufacturerData.Length; at += TokenBytes)
        {
            if (Matches(key, manufacturerData.Slice(at, TokenBytes), now)) return true;
        }
        return false;
    }
}

public enum PresenceState
{
    /// <summary>Not seen near since the last unlock: never locks.</summary>
    Waiting,

    /// <summary>The phone is near; walking away will lock.</summary>
    Near,

    /// <summary>Armed and the phone is weak or gone: locks when the countdown ends.</summary>
    Leaving,

    /// <summary>Locked by us; waits for the next unlock.</summary>
    Locked,
}

/// <summary>
/// Decides when to lock the PC. Signal strength is smoothed (one weak reading never counts). It arms only once the
/// phone has been seen near since the session was unlocked, and locks after the phone has been weak or unseen for
/// <see cref="Delay"/>; never while <c>blocked</c> (a call or a transfer). After locking it waits for the next unlock.
/// </summary>
public sealed class PresenceMonitor
{
    private const double Smoothing = 0.3;
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(8);

    private double? _signal;
    private DateTimeOffset _lastSeen;
    private DateTimeOffset _lastNear;
    private bool _armed;
    private bool _locked;

    /// <summary>Smoothed signal at or above this (dBm) counts as near.</summary>
    public int NearRssi { get; set; } = -75;

    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Smoothed signal, or null when the phone hasn't been heard lately.</summary>
    public double? Signal => _signal;

    public PresenceState State => _locked ? PresenceState.Locked : !_armed ? PresenceState.Waiting : IsNear ? PresenceState.Near : PresenceState.Leaving;

    public bool IsNear => _signal is { } s && s >= NearRssi;

    /// <summary>Time left before locking while <see cref="PresenceState.Leaving"/>.</summary>
    public TimeSpan Remaining(DateTimeOffset now) => _armed && !IsNear ? Max(TimeSpan.Zero, Delay - (now - _lastNear)) : Delay;

    /// <summary>A beacon from the phone was heard.</summary>
    public void Sample(int rssi, DateTimeOffset now)
    {
        _signal = _signal is { } s ? Smoothing * rssi + (1 - Smoothing) * s : rssi;
        _lastSeen = now;
        if (IsNear)
        {
            _lastNear = now;
            if (!_locked) _armed = true;
        }
    }

    /// <summary>Call about once a second while armed; true means lock now.</summary>
    public bool Tick(DateTimeOffset now, bool blocked)
    {
        if (_signal is not null && now - _lastSeen > Stale) _signal = null; // no beacon lately: gone
        if (!_armed || _locked) return false;
        if (blocked || IsNear)
        {
            if (blocked) _lastNear = now; // a call or transfer restarts the countdown
            return false;
        }
        if (now - _lastNear < Delay) return false;
        _armed = false;
        _locked = true;
        return true;
    }

    /// <summary>The user unlocked the PC: wait until the phone is near again before arming.</summary>
    public void Unlocked()
    {
        _locked = false;
        _armed = false;
    }

    /// <summary>The PC was locked (by anyone): nothing to do until it's unlocked.</summary>
    public void Locked()
    {
        _locked = true;
        _armed = false;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
