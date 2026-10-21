using System.Net.Sockets;
using System.Threading.Channels;
using Midfielder.Core.Crypto;
using Midfielder.Core.Wire;
using Midfielder.Protocol;

namespace Midfielder.Core.Net;

public sealed class NotConnectedException : IOException
{
    public NotConnectedException() { }
    public NotConnectedException(string message) : base(message) { }
    public NotConnectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One authenticated control connection. Three tasks per session: a reader awaiting socket data, a writer
/// awaiting a bounded queue, and a heartbeat that sleeps for minutes. <see cref="Close"/> cancels all three
/// and disposes the stream, so nothing outlives the session.
/// </summary>
public sealed class PeerSession : IDisposable
{
    private readonly Stream _stream;
    private readonly TcpClient _client;
    private readonly NodeConfig _config;
    private readonly Func<PeerSession, Envelope, ValueTask> _handler;
    private readonly Channel<Envelope> _outgoing;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastInboundMs;
    private int _isClosed;
    private Task _loops = Task.CompletedTask;

    internal PeerSession(TcpClient client, Stream stream, PairedDevice peer, bool isDialer, HostPort remote, NodeConfig config,
        Func<PeerSession, Envelope, ValueTask> handler)
    {
        _client = client;
        _stream = stream;
        Peer = peer;
        IsDialer = isDialer;
        Remote = remote;
        _config = config;
        _handler = handler;
        _outgoing = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(config.SendQueueCapacity) { SingleReader = true });
        _lastInboundMs = config.Clock();
    }

    public PairedDevice Peer { get; }

    private volatile IReadOnlySet<string> _capabilities = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the peer supports and has switched on (from its Hello, replaced by FeaturesChanged).</summary>
    public IReadOnlySet<string> Capabilities
    {
        get => _capabilities;
        internal set => _capabilities = value;
    }

    public bool Has(string capability) => _capabilities.Contains(capability);

    /// <summary>True if we opened the connection; the dialer drives heartbeats and opens data connections.</summary>
    public bool IsDialer { get; }

    public HostPort Remote { get; }
    public bool IsOpen => Volatile.Read(ref _isClosed) == 0;

    /// <summary>Completes with the close cause (null for a clean close).</summary>
    public Task<Exception?> Closed => _closed.Task;

    internal void Start()
    {
        var ct = _cts.Token;
        _loops = Task.WhenAll(
            Task.Run(() => WriterLoopAsync(ct), CancellationToken.None),
            Task.Run(() => ReaderLoopAsync(ct), CancellationToken.None),
            Task.Run(() => HeartbeatLoopAsync(ct), CancellationToken.None));
    }

    public async ValueTask SendAsync(Envelope envelope, CancellationToken ct = default)
    {
        try
        {
            await _outgoing.Writer.WriteAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException e)
        {
            throw new NotConnectedException($"session to {Peer.Name} is closed", e);
        }
    }

    public bool TrySend(Envelope envelope) => _outgoing.Writer.TryWrite(envelope);

    /// <summary>Checks liveness now (after resume from sleep or a network change).</summary>
    public void Probe()
    {
        if (!IsOpen) return;
        long sentAt = _config.Clock();
        TrySend(new Envelope { Ping = new Ping { SentAtMs = sentAt } });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_config.HeartbeatTimeoutMs), _cts.Token).ConfigureAwait(false);
                if (Interlocked.Read(ref _lastInboundMs) < sentAt) Close(new IOException("probe timed out"));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Close(Exception? cause = null)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        _stream.Dispose();
        _client.Dispose();
        _closed.TrySetResult(cause);
        // Dispose the token source only once every loop has observed the cancellation.
        _ = _loops.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() => Close();

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _outgoing.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Drain everything queued, then flush once: fewer syscalls and TLS records.
                while (reader.TryRead(out var env)) await Envelopes.WriteAsync(_stream, env, _config.MaxFrameBytes, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var env = await Envelopes.ReadAsync(_stream, _config.MaxFrameBytes, ct).ConfigureAwait(false);
                if (env is null) break;
                Interlocked.Exchange(ref _lastInboundMs, _config.Clock());
                switch (env.BodyCase)
                {
                    case Envelope.BodyOneofCase.Ping:
                        TrySend(new Envelope { Pong = new Pong { EchoSentAtMs = env.Ping.SentAtMs } });
                        break;
                    case Envelope.BodyOneofCase.Pong:
                        break;
                    case Envelope.BodyOneofCase.Bye:
                        Close();
                        return;
                    default:
                        await _handler(this, env).ConfigureAwait(false);
                        break;
                }
            }
            Close();
        }
        catch (Exception e)
        {
            Close(e);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (IsDialer)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_config.HeartbeatIntervalMs), ct).ConfigureAwait(false);
                    if (_config.Clock() - Interlocked.Read(ref _lastInboundMs) < _config.HeartbeatIntervalMs) continue;
                    TrySend(new Envelope { Ping = new Ping { SentAtMs = _config.Clock() } });
                    await Task.Delay(TimeSpan.FromMilliseconds(_config.HeartbeatTimeoutMs), ct).ConfigureAwait(false);
                    if (_config.Clock() - Interlocked.Read(ref _lastInboundMs) >= _config.HeartbeatIntervalMs + _config.HeartbeatTimeoutMs)
                    {
                        Close(new IOException("heartbeat timed out"));
                        return;
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_config.ListenerIdleTimeoutMs / 2), ct).ConfigureAwait(false);
                    if (_config.Clock() - Interlocked.Read(ref _lastInboundMs) >= _config.ListenerIdleTimeoutMs)
                    {
                        Close(new IOException("peer silent for too long"));
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override string ToString() => $"PeerSession({Peer.Name}, dialer={IsDialer}, open={IsOpen})";
}
