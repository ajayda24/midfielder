using System.Collections.Concurrent;
using Midfielder.Core.Crypto;
using Midfielder.Protocol;

namespace Midfielder.Core.Net;

public sealed record PairedDevice(Fingerprint Fingerprint, string Name, DeviceKind Kind, string? LastAddress = null)
{
    public string DeviceId => Fingerprint.DeviceId;
}

/// <summary>Pinned peers. The app persists this with DPAPI.</summary>
public interface ITrustStore
{
    PairedDevice? Get(Fingerprint fingerprint);
    void Put(PairedDevice device);
    void Remove(Fingerprint fingerprint);
    IReadOnlyList<PairedDevice> All();
    bool IsTrusted(Fingerprint fingerprint) => Get(fingerprint) is not null;
}

public class InMemoryTrustStore : ITrustStore
{
    private readonly ConcurrentDictionary<Fingerprint, PairedDevice> _map = new();
    public PairedDevice? Get(Fingerprint fingerprint) => _map.GetValueOrDefault(fingerprint);
    public virtual void Put(PairedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _map[device.Fingerprint] = device;
    }
    public virtual void Remove(Fingerprint fingerprint) => _map.TryRemove(fingerprint, out _);
    public IReadOnlyList<PairedDevice> All() => [.. _map.Values];
    public bool IsTrusted(Fingerprint fingerprint) => _map.ContainsKey(fingerprint);
}

public sealed record NodeConfig
{
    public int MaxFrameBytes { get; init; } = Limits.MaxFrameBytes;
    public int ConnectTimeoutMs { get; init; } = 5_000;
    public int HandshakeTimeoutMs { get; init; } = 10_000;
    public int DataStallTimeoutMs { get; init; } = 30_000;
    /// <summary>The dialer pings only after this much silence: an idle radio is a sleeping radio.</summary>
    public long HeartbeatIntervalMs { get; init; } = 4 * 60_000;
    public long HeartbeatTimeoutMs { get; init; } = 20_000;
    /// <summary>
    /// The listener never pings; it drops a session silent this long. Long on purpose: a phone in deep sleep
    /// can't ping, and dropping it would only cause a reconnect cycle. Dead sessions are replaced on reconnect.
    /// </summary>
    public long ListenerIdleTimeoutMs { get; init; } = 45 * 60_000;
    public int SendQueueCapacity { get; init; } = 64;
    public int MaxConnections { get; init; } = 16;
    public int DataRetryAttempts { get; init; } = 5;
    public long AnswerTimeoutMs { get; init; } = 2 * 60_000;
    public long ResumeWindowMs { get; init; } = 10 * 60_000;
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;
}
