using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Midfielder.Core.Crypto;
using Midfielder.Core.Discovery;
using Midfielder.Core.Transfer;
using Midfielder.Core.Wire;
using Midfielder.Protocol;

namespace Midfielder.Core.Net;

/// <summary>What the app hears about. Callbacks run on thread-pool threads; keep them short.</summary>
public interface INodeEvents
{
    void OnPaired(PairedDevice device) { }
    void OnPeerConnected(PeerSession session) { }
    void OnPeerDisconnected(PairedDevice device, Exception? cause) { }
    ValueTask OnClipboardAsync(PairedDevice from, ClipboardItem item) => ValueTask.CompletedTask;

    /// <summary>Return one sink per file to accept, or null to decline. Names are already sanitized.</summary>
    ValueTask<IReadOnlyList<IFileSink>?> OnTransferOfferAsync(PairedDevice from, TransferOffer offer, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<IFileSink>?>(null);

    void OnTransferProgress(string transferId, long transferred, long total) { }
    void OnTransferFinished(string transferId, PairedDevice from, TransferResult result) { }

    /// <summary>
    /// An optional-feature message (notifications, battery, ring, media) or a FeaturesChanged update (the session's
    /// <see cref="PeerSession.Capabilities"/> are already updated). Runs on the session's reader; keep it short.
    /// </summary>
    void OnFeature(PeerSession from, Envelope env) { }
}

public abstract record TransferResult
{
    public sealed record Completed(IReadOnlyList<string> Locations) : TransferResult;
    public sealed record Failed(string Reason) : TransferResult;
    public sealed record Cancelled : TransferResult;
}

/// <summary>
/// A Midfielder device on the network (same protocol and behavior as the Kotlin MidfielderNode).
/// The Windows app listens; the phone dials, so the phone never needs an open port.
/// </summary>
public sealed class MidfielderNode : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly string _deviceName;
    private readonly DeviceKind _kind;
    private readonly ITrustStore _trust;
    private readonly INodeEvents _events;
    private readonly NodeConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Fingerprint, PeerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferAnswer>> _pendingAnswers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly SslServerAuthenticationOptions _serverOptions;
    private volatile PairingWindow? _pairing;
    private TcpListener? _listener;

    public MidfielderNode(DeviceIdentity identity, string deviceName, DeviceKind kind, ITrustStore trust, INodeEvents events, NodeConfig? config = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _deviceName = SanitizeName(deviceName);
        _kind = kind;
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _config = config ?? new NodeConfig();
        _slots = new SemaphoreSlim(_config.MaxConnections, _config.MaxConnections);
        _serverOptions = Tls.ServerOptions(identity, fp => _trust.IsTrusted(fp) || _pairing?.IsOpen(_config.Clock()) == true);
    }

    public Fingerprint Fingerprint => _identity.Fingerprint;

    public IReadOnlyList<PairedDevice> ConnectedPeers => [.. _sessions.Values.Where(s => s.IsOpen).Select(s => s.Peer)];

    public event Action? ConnectedPeersChanged;

    public PeerSession? Session(Fingerprint fp) => _sessions.TryGetValue(fp, out var s) && s.IsOpen ? s : null;

    // ---------------------------------------------------------------------------------------------
    // Listener role
    // ---------------------------------------------------------------------------------------------

    /// <summary>Starts accepting connections; returns the bound port (pass 0 for any free port).</summary>
    public int StartListening(int port, IPAddress? bindAddress = null)
    {
        if (_listener is not null) throw new InvalidOperationException("already listening");
        bool dualStack = bindAddress is null && Socket.OSSupportsIPv6;
        var listener = new TcpListener(bindAddress ?? (dualStack ? IPAddress.IPv6Any : IPAddress.Any), port);
        if (dualStack) listener.Server.DualMode = true;
        listener.Start(backlog: 50);
        _listener = listener;
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            if (!_slots.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleInboundAsync(client, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    client.Dispose();
                }
                finally
                {
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Opens a one-time pairing window and returns the invite to show as a QR code.</summary>
    public PairingInvite OpenPairing(IReadOnlyList<HostPort> addresses, long ttlMs = 5 * 60_000)
    {
        var invite = new PairingInvite(_identity.Fingerprint, PairingInvite.NewSecret(), addresses, _deviceName);
        _pairing = new PairingWindow(invite.Secret, _config.Clock() + ttlMs);
        return invite;
    }

    public void ClosePairing() => _pairing = null;

    private async Task HandleInboundAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        Fingerprint peerFp;
        Envelope first;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(_config.HandshakeTimeoutMs);
            try
            {
                await ssl.AuthenticateAsServerAsync(_serverOptions, handshake.Token).ConfigureAwait(false);
                peerFp = Fingerprint.Of(ssl.RemoteCertificate ?? throw new ProtocolException("no client certificate"));
                first = await Envelopes.RequireAsync(ssl, Limits.MaxHandshakeFrameBytes, "first frame", handshake.Token).ConfigureAwait(false);
                if (first.BodyCase == Envelope.BodyOneofCase.Hello)
                {
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    var ip = remote?.Address;
                    if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4(); // "::ffff:192.168.1.5" -> "192.168.1.5"
                    var hp = new HostPort(ip?.ToString() ?? "unknown", remote?.Port ?? 1);
                    var session = await AcceptControlAsync(client, ssl, peerFp, first.Hello, hp, handshake.Token).ConfigureAwait(false);
                    handshake.Dispose();
                    await session.Closed.ConfigureAwait(false);
                    return;
                }
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        await using (ssl.ConfigureAwait(false))
        {
            if (first.BodyCase == Envelope.BodyOneofCase.AudioOpen)
            {
                if (!_trust.IsTrusted(peerFp)) throw new ProtocolException("stream connection from unpaired peer");
                string id = first.AudioOpen.StreamId;
                if (!_streams.TryGetValue(id, out var stream) || stream.Peer != peerFp) throw new ProtocolException("unknown stream");
                try
                {
                    await stream.Writer(ssl, ct).ConfigureAwait(false);
                }
                finally
                {
                    _streams.TryRemove(new KeyValuePair<string, ServedStream>(id, stream));
                }
                client.Dispose();
                return;
            }
            if (first.BodyCase != Envelope.BodyOneofCase.DataOpen) throw new ProtocolException("unexpected first frame");
            if (!_trust.IsTrusted(peerFp)) throw new ProtocolException("data connection from unpaired peer");
            await ServeDataAsync(ssl, peerFp, first.DataOpen, ct).ConfigureAwait(false);
        }
        client.Dispose();
    }

    private async Task<PeerSession> AcceptControlAsync(TcpClient client, SslStream ssl, Fingerprint peerFp, Hello hello, HostPort remote, CancellationToken ct)
    {
        ValidateHello(hello, peerFp);
        var device = _trust.Get(peerFp);
        if (device is null)
        {
            var window = _pairing;
            if (window is null || !window.IsOpen(_config.Clock())) throw new ProtocolException("unpaired peer and no pairing window");
            _pairing = null; // single use, whatever the outcome
            var request = (await Envelopes.RequireAsync(ssl, Limits.MaxHandshakeFrameBytes, "PairRequest", ct).ConfigureAwait(false)).PairRequest
                ?? throw new ProtocolException("expected PairRequest");
            byte[] expected = PairingProof.Client(window.Secret, peerFp, _identity.Fingerprint);
            if (!CryptographicOperations.FixedTimeEquals(expected, request.Proof.Span))
            {
                await WriteNowAsync(ssl, new Envelope { PairResult = new PairResult { Accepted = false, Reason = "wrong pairing code" } }, ct).ConfigureAwait(false);
                throw new ProtocolException("bad pairing proof");
            }
            device = new PairedDevice(peerFp, SanitizeName(hello.DeviceName), hello.Kind);
            _trust.Put(device);
            byte[] proof = PairingProof.Server(window.Secret, peerFp, _identity.Fingerprint);
            await WriteNowAsync(ssl, new Envelope { PairResult = new PairResult { Accepted = true, Proof = ByteString.CopyFrom(proof) } }, ct).ConfigureAwait(false);
            _events.OnPaired(device);
        }
        else
        {
            device = device with { Name = SanitizeName(hello.DeviceName), Kind = hello.Kind };
            _trust.Put(device);
        }
        await WriteNowAsync(ssl, new Envelope { Hello = MyHello() }, ct).ConfigureAwait(false);
        return StartSession(client, ssl, device, isDialer: false, remote, hello.Capabilities);
    }

    // ---------------------------------------------------------------------------------------------
    // Dialer role
    // ---------------------------------------------------------------------------------------------

    public async Task<PeerSession> PairAsync(PairingInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Exception? last = null;
        foreach (var address in invite.Addresses)
        {
            try
            {
                return await DialAsync(address, invite.Fingerprint, invite, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw new IOException($"could not reach {invite.Name}", last);
    }

    public Task<PeerSession> ConnectAsync(PairedDevice device, HostPort address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_trust.IsTrusted(device.Fingerprint)) throw new InvalidOperationException("device is not paired");
        return DialAsync(address, device.Fingerprint, null, ct);
    }

    private async Task<PeerSession> DialAsync(HostPort address, Fingerprint expected, PairingInvite? invite, CancellationToken ct)
    {
        var (client, ssl) = await OpenTlsAsync(address, expected, ct).ConfigureAwait(false);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(_config.HandshakeTimeoutMs);
            await Envelopes.WriteAsync(ssl, new Envelope { Hello = MyHello() }, Limits.MaxHandshakeFrameBytes, handshake.Token).ConfigureAwait(false);
            if (invite is not null)
            {
                byte[] proof = PairingProof.Client(invite.Secret, _identity.Fingerprint, expected);
                await Envelopes.WriteAsync(ssl, new Envelope { PairRequest = new PairRequest { Proof = ByteString.CopyFrom(proof) } }, Limits.MaxHandshakeFrameBytes, handshake.Token).ConfigureAwait(false);
            }
            await ssl.FlushAsync(handshake.Token).ConfigureAwait(false);
            if (invite is not null)
            {
                var result = (await Envelopes.RequireAsync(ssl, Limits.MaxHandshakeFrameBytes, "PairResult", handshake.Token).ConfigureAwait(false)).PairResult
                    ?? throw new ProtocolException("expected PairResult");
                if (!result.Accepted) throw new IOException($"pairing refused: {Truncate(result.Reason, 100)}");
                byte[] want = PairingProof.Server(invite.Secret, _identity.Fingerprint, expected);
                if (!CryptographicOperations.FixedTimeEquals(want, result.Proof.Span))
                    throw new ProtocolException("peer could not prove it knows the pairing code");
            }
            var hello = (await Envelopes.RequireAsync(ssl, Limits.MaxHandshakeFrameBytes, "Hello", handshake.Token).ConfigureAwait(false)).Hello
                ?? throw new ProtocolException("expected Hello");
            ValidateHello(hello, expected);
            var device = new PairedDevice(expected, SanitizeName(hello.DeviceName), hello.Kind, address.ToString());
            _trust.Put(device);
            if (invite is not null) _events.OnPaired(device);
            return StartSession(client, ssl, device, isDialer: true, address, hello.Capabilities);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    private async Task<(TcpClient, SslStream)> OpenTlsAsync(HostPort address, Fingerprint expected, CancellationToken ct)
    {
        // Address family is chosen from the resolved address, so this works on IPv4-only machines too.
        var client = new TcpClient { NoDelay = true };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        SslStream? ssl = null;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(_config.ConnectTimeoutMs);
                await client.ConnectAsync(address.Host, address.Port, connect.Token).ConfigureAwait(false);
            }
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(_config.HandshakeTimeoutMs);
            await ssl.AuthenticateAsClientAsync(Tls.ClientOptions(_identity, address.Host, expected), handshake.Token).ConfigureAwait(false);
            return (client, ssl);
        }
        catch
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Sessions
    // ---------------------------------------------------------------------------------------------

    private PeerSession StartSession(TcpClient client, SslStream ssl, PairedDevice device, bool isDialer, HostPort remote,
        IEnumerable<string> capabilities)
    {
        var session = new PeerSession(client, ssl, device, isDialer, remote, _config, DispatchAsync) { Capabilities = CapabilitySet(capabilities) };
        PeerSession? previous = null;
        _sessions.AddOrUpdate(device.Fingerprint, session, (_, old) =>
        {
            previous = old;
            return session;
        });
        previous?.Close(new IOException("replaced by a new connection"));
        session.Start();
        ConnectedPeersChanged?.Invoke();
        _ = session.Closed.ContinueWith(t =>
        {
            _sessions.TryRemove(new KeyValuePair<Fingerprint, PeerSession>(device.Fingerprint, session));
            ConnectedPeersChanged?.Invoke();
            _events.OnPeerDisconnected(device, t.Result);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _events.OnPeerConnected(session);
        foreach (var t in _outgoing.Values)
        {
            if (t.Peer == device.Fingerprint && t.Job is null && !t.Result.Task.IsCompleted) StartOutgoing(session, t);
        }
        return session;
    }

    private async ValueTask DispatchAsync(PeerSession session, Envelope env)
    {
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.Clipboard:
                try
                {
                    await _events.OnClipboardAsync(session.Peer, env.Clipboard).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failure applying one clipboard item must never take the whole connection down.
                }
                break;
            case Envelope.BodyOneofCase.TransferOffer:
                var offer = env.TransferOffer;
                _ = Task.Run(() => HandleOfferAsync(session, offer));
                break;
            case Envelope.BodyOneofCase.TransferAnswer:
                if (_pendingAnswers.TryGetValue(env.TransferAnswer.TransferId, out var tcs)) tcs.TrySetResult(env.TransferAnswer);
                break;
            case Envelope.BodyOneofCase.TransferCancel:
                HandleCancel(session.Peer, env.TransferCancel.TransferId);
                break;
            case Envelope.BodyOneofCase.FeaturesChanged:
                session.Capabilities = CapabilitySet(env.FeaturesChanged.Capabilities);
                Feature(session, env);
                break;
            case Envelope.BodyOneofCase.NotificationPosted:
            case Envelope.BodyOneofCase.NotificationRemoved:
            case Envelope.BodyOneofCase.NotificationAction:
            case Envelope.BodyOneofCase.BatteryStatus:
            case Envelope.BodyOneofCase.RingRequest:
            case Envelope.BodyOneofCase.MediaState:
            case Envelope.BodyOneofCase.MediaCommand:
            case Envelope.BodyOneofCase.AudioRequest:
            case Envelope.BodyOneofCase.AudioOffer:
            case Envelope.BodyOneofCase.AudioStop:
            case Envelope.BodyOneofCase.CallState:
            case Envelope.BodyOneofCase.CallCommand:
            case Envelope.BodyOneofCase.PresenceKey:
                Feature(session, env);
                break;
            default:
                break; // unknown or out of place: ignore for forward compatibility
        }
    }

    private void Feature(PeerSession session, Envelope env)
    {
        try
        {
            _events.OnFeature(session, env);
        }
        catch (Exception)
        {
            // A bad feature message must never take the connection down.
        }
    }

    private const int MaxCapabilities = 64;
    private static readonly string[] CoreCapabilities = ["clipboard.text", "clipboard.image", "file"];
    private volatile string[] _features = [];

    private static HashSet<string> CapabilitySet(IEnumerable<string> list) => new(list.Take(MaxCapabilities), StringComparer.Ordinal);

    /// <summary>
    /// Sets the optional features this device has switched on. New sessions advertise them in Hello; connected peers
    /// are told right away.
    /// </summary>
    public void SetFeatures(IEnumerable<string> enabled)
    {
        var sorted = enabled.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sorted.SequenceEqual(_features)) return;
        _features = sorted;
        var changed = new FeaturesChanged();
        changed.Capabilities.Add(AllCapabilities());
        var env = new Envelope { FeaturesChanged = changed };
        foreach (var s in _sessions.Values)
        {
            if (s.IsOpen) s.TrySend(env);
        }
    }

    /// <summary>
    /// Sends a feature message to every connected peer that advertises <paramref name="capability"/>. Droppable: a full
    /// queue skips that peer. Returns how many peers it was queued for.
    /// </summary>
    public int SendFeature(string capability, Envelope env)
    {
        int queued = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.IsOpen && s.Has(capability) && s.TrySend(env)) queued++;
        }
        return queued;
    }

    private IEnumerable<string> AllCapabilities() => CoreCapabilities.Concat(_features);

    /// <summary>
    /// Sends a clipboard item to every connected peer; a slow peer never blocks the others.
    /// Returns how many peers it was queued for.
    /// </summary>
    public int BroadcastClipboard(ClipboardItem item)
    {
        var env = new Envelope { Clipboard = item };
        int queued = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.IsOpen && s.TrySend(env)) queued++;
        }
        return queued;
    }

    // ---------------------------------------------------------------------------------------------
    // File transfer
    // ---------------------------------------------------------------------------------------------

    private sealed class OutgoingTransfer(string id, Fingerprint peer, TransferOffer offer, IReadOnlyList<IFileSource> sources)
    {
        private readonly bool[] _done = new bool[sources.Count];
        public string Id { get; } = id;
        public Fingerprint Peer { get; } = peer;
        public TransferOffer Offer { get; } = offer;
        public IReadOnlyList<IFileSource> Sources { get; } = sources;
        public TaskCompletionSource<TransferResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile Task? Job;
        public CancellationTokenSource Cancel { get; } = new();

        public bool MarkDone(int i)
        {
            lock (_done)
            {
                _done[i] = true;
                return _done.All(d => d);
            }
        }

        public bool IsDone(int i)
        {
            lock (_done) return _done[i];
        }
    }

    private sealed class IncomingTransfer(string id, PairedDevice from, TransferOffer offer, IReadOnlyList<IFileSink> sinks, long touchedAt)
    {
        private readonly string?[] _locations = new string?[sinks.Count];
        public string Id { get; } = id;
        public PairedDevice From { get; } = from;
        public TransferOffer Offer { get; } = offer;
        public IReadOnlyList<IFileSink> Sinks { get; } = sinks;
        public long TouchedAt = touchedAt;

        public bool MarkDone(int i, string location)
        {
            lock (_locations)
            {
                _locations[i] = location;
                return _locations.All(l => l is not null);
            }
        }

        public bool IsDone(int i)
        {
            lock (_locations) return _locations[i] is not null;
        }

        public IReadOnlyList<string> Locations()
        {
            lock (_locations) return [.. _locations.Where(l => l is not null).Select(l => l!)];
        }
    }

    /// <summary>
    /// Sends files and completes when they are delivered, declined or failed. A dropped connection resumes
    /// from the last received byte when the peer reconnects within the resume window.
    /// </summary>
    public async Task<TransferResult> SendFilesAsync(Fingerprint peer, IReadOnlyList<IFileSource> sources,
        TransferPurpose purpose = TransferPurpose.File, string? transferId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (transferId is not null && !Hex.IsLowerHex(transferId, Limits.TransferIdHexChars)) throw new ArgumentException("bad transfer id", nameof(transferId));
        if (sources.Count is < 1 or > Limits.MaxFilesPerOffer) throw new ArgumentException($"1..{Limits.MaxFilesPerOffer} files");
        var session = Session(peer) ?? throw new NotConnectedException("peer not connected");
        string id = transferId ?? NewTransferId();
        var offer = new TransferOffer { TransferId = id, Purpose = purpose };
        foreach (var s in sources)
        {
            offer.Files.Add(new FileEntry
            {
                Name = FileNameSanitizer.Sanitize(s.Name),
                Size = checked((ulong)s.Size),
                Mime = Truncate(s.Mime, 100),
                ModifiedMs = s.ModifiedMs,
            });
        }
        var t = new OutgoingTransfer(id, peer, offer, sources);
        _outgoing[id] = t;
        using var reg = ct.Register(() => CancelTransfer(id));
        try
        {
            StartOutgoing(session, t);
            return await t.Result.Task.ConfigureAwait(false);
        }
        finally
        {
            _outgoing.TryRemove(id, out _);
            await t.Cancel.CancelAsync().ConfigureAwait(false);
            t.Cancel.Dispose();
        }
    }

    private void StartOutgoing(PeerSession session, OutgoingTransfer t)
    {
        // Disposed when this job ends: an undisposed linked source would leave a registration on the node's
        // long-lived token for every transfer.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, t.Cancel.Token);
        var ct = linked.Token;
        t.Job = Task.Run(async () =>
        {
            using var _ = linked;
            try
            {
                var answer = await RequestAnswerAsync(session, t.Offer, ct).ConfigureAwait(false);
                if (!answer.Accepted)
                {
                    t.Result.TrySetResult(new TransferResult.Failed($"declined: {Truncate(answer.Reason, 100)}"));
                    return;
                }
                if (session.IsDialer)
                {
                    for (int i = 0; i < t.Sources.Count; i++)
                    {
                        if (t.IsDone(i)) continue;
                        int index = i;
                        await RunDataAsDialerAsync(session, t.Id, index, (net, c) =>
                            TransferEngine.SendAsync(net, t.Sources[index], t.Offer.Files[index], ProgressFor(t.Id, t.Offer, index), _config.DataStallTimeoutMs, c), ct).ConfigureAwait(false);
                        t.MarkDone(index);
                    }
                    t.Result.TrySetResult(new TransferResult.Completed([]));
                }
                else
                {
                    // The peer dials data connections to us; ServeDataAsync completes the result.
                    var winner = await Task.WhenAny(t.Result.Task, session.Closed).ConfigureAwait(false);
                    if (winner != t.Result.Task) throw new NotConnectedException("disconnected");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                if (session.IsOpen && e is not NotConnectedException) t.Result.TrySetResult(new TransferResult.Failed(e.Message));
                else PauseOutgoing(t);
            }
        }, CancellationToken.None);
    }

    private void PauseOutgoing(OutgoingTransfer t)
    {
        t.Job = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_config.ResumeWindowMs), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            if (t.Job is null) t.Result.TrySetResult(new TransferResult.Failed("peer disconnected"));
        });
    }

    private async Task<TransferAnswer> RequestAnswerAsync(PeerSession session, TransferOffer offer, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<TransferAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAnswers[offer.TransferId] = tcs;
        try
        {
            await session.SendAsync(new Envelope { TransferOffer = offer }, ct).ConfigureAwait(false);
            var timeout = Task.Delay(TimeSpan.FromMilliseconds(_config.AnswerTimeoutMs), ct);
            var winner = await Task.WhenAny(tcs.Task, session.Closed, timeout).ConfigureAwait(false);
            if (winner == tcs.Task) return await tcs.Task.ConfigureAwait(false);
            if (winner == session.Closed) throw new NotConnectedException("disconnected while waiting for answer");
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("no answer from peer");
        }
        finally
        {
            _pendingAnswers.TryRemove(new KeyValuePair<string, TaskCompletionSource<TransferAnswer>>(offer.TransferId, tcs));
        }
    }

    private async Task HandleOfferAsync(PeerSession session, TransferOffer raw)
    {
        try
        {
            string id = raw.TransferId;
            var from = session.Peer;
            bool valid = Hex.IsLowerHex(id, Limits.TransferIdHexChars) && raw.Files.Count is >= 1 and <= Limits.MaxFilesPerOffer
                && raw.Files.All(f => f.Size <= long.MaxValue);
            if (!valid)
            {
                session.TrySend(new Envelope { TransferAnswer = new TransferAnswer { TransferId = Truncate(id, 64), Accepted = false, Reason = "invalid offer" } });
                return;
            }
            if (_incoming.TryGetValue(id, out var existing))
            {
                bool ok = existing.From.Fingerprint == from.Fingerprint;
                Interlocked.Exchange(ref existing.TouchedAt, _config.Clock());
                await session.SendAsync(new Envelope { TransferAnswer = new TransferAnswer { TransferId = id, Accepted = ok, Reason = ok ? "" : "id in use" } }).ConfigureAwait(false);
                if (ok && session.IsDialer) StartIncomingAsDialer(session, existing);
                return;
            }
            SweepStaleIncoming();
            var offer = raw.Clone();
            foreach (var f in offer.Files) f.Name = FileNameSanitizer.Sanitize(f.Name);
            IReadOnlyList<IFileSink>? sinks;
            using (var decide = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                decide.CancelAfter(TimeSpan.FromMilliseconds(_config.AnswerTimeoutMs));
                try
                {
                    sinks = await _events.OnTransferOfferAsync(from, offer, decide.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    sinks = null;
                }
            }
            if (sinks is null || sinks.Count != offer.Files.Count)
            {
                await session.SendAsync(new Envelope { TransferAnswer = new TransferAnswer { TransferId = id, Accepted = false, Reason = "declined" } }).ConfigureAwait(false);
                return;
            }
            var t = new IncomingTransfer(id, from, offer, sinks, _config.Clock());
            _incoming[id] = t;
            await session.SendAsync(new Envelope { TransferAnswer = new TransferAnswer { TransferId = id, Accepted = true } }).ConfigureAwait(false);
            if (session.IsDialer) StartIncomingAsDialer(session, t);
        }
        catch (Exception)
        {
            // Session closed mid-offer; the sender re-offers on reconnect.
        }
    }

    private void StartIncomingAsDialer(PeerSession session, IncomingTransfer t)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < t.Sinks.Count; i++)
                {
                    if (t.IsDone(i)) continue;
                    int index = i;
                    await RunDataAsDialerAsync(session, t.Id, index, async (net, c) =>
                    {
                        string location = await TransferEngine.ReceiveAsync(net, t.Sinks[index], t.Offer.Files[index], ProgressFor(t.Id, t.Offer, index), _config.DataStallTimeoutMs, c).ConfigureAwait(false);
                        t.MarkDone(index, location);
                    }, _cts.Token).ConfigureAwait(false);
                }
                FinishIncoming(t, null);
            }
            catch (Exception e)
            {
                // Peer gone: keep partial data, the sender re-offers the same id when it reconnects.
                if (session.IsOpen) FinishIncoming(t, e.Message);
            }
        });
    }

    private void FinishIncoming(IncomingTransfer t, string? failure)
    {
        if (!_incoming.TryRemove(new KeyValuePair<string, IncomingTransfer>(t.Id, t))) return;
        if (failure is null)
        {
            _events.OnTransferFinished(t.Id, t.From, new TransferResult.Completed(t.Locations()));
            return;
        }
        DiscardUnfinished(t);
        _events.OnTransferFinished(t.Id, t.From, new TransferResult.Failed(failure));
    }

    private static void DiscardUnfinished(IncomingTransfer t)
    {
        for (int i = 0; i < t.Sinks.Count; i++)
        {
            if (t.IsDone(i)) continue;
            try
            {
                t.Sinks[i].Discard();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Lazy cleanup instead of a timer: nothing wakes the CPU just to tidy up.</summary>
    private void SweepStaleIncoming()
    {
        long now = _config.Clock();
        foreach (var t in _incoming.Values)
        {
            if (now - Interlocked.Read(ref t.TouchedAt) > _config.ResumeWindowMs) FinishIncoming(t, "expired");
        }
    }

    private async Task ServeDataAsync(Stream net, Fingerprint peerFp, DataOpen open, CancellationToken ct)
    {
        int index = checked((int)open.FileIndex);
        if (_outgoing.TryGetValue(open.TransferId, out var o) && o.Peer == peerFp)
        {
            if (index < 0 || index >= o.Sources.Count) throw new ProtocolException("bad file index");
            await TransferEngine.SendAsync(net, o.Sources[index], o.Offer.Files[index], ProgressFor(o.Id, o.Offer, index), _config.DataStallTimeoutMs, ct).ConfigureAwait(false);
            if (o.MarkDone(index)) o.Result.TrySetResult(new TransferResult.Completed([]));
            return;
        }
        if (_incoming.TryGetValue(open.TransferId, out var inc) && inc.From.Fingerprint == peerFp)
        {
            if (index < 0 || index >= inc.Sinks.Count) throw new ProtocolException("bad file index");
            Interlocked.Exchange(ref inc.TouchedAt, _config.Clock());
            string location = await TransferEngine.ReceiveAsync(net, inc.Sinks[index], inc.Offer.Files[index], ProgressFor(inc.Id, inc.Offer, index), _config.DataStallTimeoutMs, ct).ConfigureAwait(false);
            if (inc.MarkDone(index, location)) FinishIncoming(inc, null);
            return;
        }
        throw new ProtocolException("unknown transfer");
    }

    // ---------------------------------------------------------------------------------------------
    // Streams (live audio): one data connection per stream, opened by the dialer
    // ---------------------------------------------------------------------------------------------

    private sealed record ServedStream(Fingerprint Peer, Func<Stream, CancellationToken, Task> Writer);

    private readonly ConcurrentDictionary<string, ServedStream> _streams = new(StringComparer.Ordinal);

    /// <summary>
    /// Listener side: when <paramref name="peer"/> opens a data connection for <paramref name="streamId"/>,
    /// <paramref name="writer"/> runs on it and the connection closes when it returns or throws. Tell the peer with an
    /// AudioOffer.
    /// </summary>
    public void ServeStream(Fingerprint peer, string streamId, Func<Stream, CancellationToken, Task> writer) =>
        _streams[streamId] = new ServedStream(peer, writer);

    /// <summary>Stops offering a stream that hasn't been opened yet (an open one ends when its writer returns).</summary>
    public void StopServing(string streamId) => _streams.TryRemove(streamId, out _);

    /// <summary>
    /// Dialer side: opens a data connection to <paramref name="peer"/> for <paramref name="streamId"/> and runs
    /// <paramref name="reader"/> on it until it returns, the stream ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task OpenStreamAsync(Fingerprint peer, string streamId, Func<Stream, CancellationToken, Task> reader, CancellationToken ct)
    {
        var session = Session(peer) ?? throw new NotConnectedException("not connected");
        var (client, ssl) = await OpenTlsAsync(session.Remote, peer, ct).ConfigureAwait(false);
        using (client)
        {
            await using (ssl.ConfigureAwait(false))
            {
                await Envelopes.WriteAsync(ssl, new Envelope { AudioOpen = new AudioOpen { StreamId = streamId } }, Limits.MaxHandshakeFrameBytes, ct).ConfigureAwait(false);
                await ssl.FlushAsync(ct).ConfigureAwait(false);
                await reader(ssl, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunDataAsDialerAsync(PeerSession session, string transferId, int index, Func<Stream, CancellationToken, Task> block, CancellationToken ct)
    {
        var backoff = new Backoff(initialMs: 1_000, maxMs: 15_000);
        int attempt = 0;
        while (true)
        {
            try
            {
                var (client, ssl) = await OpenTlsAsync(session.Remote, session.Peer.Fingerprint, ct).ConfigureAwait(false);
                using (client)
                {
                    await using (ssl.ConfigureAwait(false))
                    {
                        await Envelopes.WriteAsync(ssl, new Envelope { DataOpen = new DataOpen { TransferId = transferId, FileIndex = (uint)index } }, Limits.MaxHandshakeFrameBytes, ct).ConfigureAwait(false);
                        await ssl.FlushAsync(ct).ConfigureAwait(false);
                        await block(ssl, ct).ConfigureAwait(false);
                    }
                }
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException && !ct.IsCancellationRequested)
            {
                attempt++;
                if (attempt >= _config.DataRetryAttempts || !session.IsOpen) throw;
                if (e is TransferFailedException) backoff.Reset();
                await Task.Delay(TimeSpan.FromMilliseconds(backoff.NextDelayMs()), ct).ConfigureAwait(false);
            }
        }
    }

    public void CancelTransfer(string transferId)
    {
        if (_outgoing.TryGetValue(transferId, out var o))
        {
            o.Result.TrySetResult(new TransferResult.Cancelled());
            o.Cancel.Cancel();
            Session(o.Peer)?.TrySend(new Envelope { TransferCancel = new TransferCancel { TransferId = transferId, Reason = "cancelled" } });
        }
        if (_incoming.TryGetValue(transferId, out var inc))
        {
            Session(inc.From.Fingerprint)?.TrySend(new Envelope { TransferCancel = new TransferCancel { TransferId = transferId, Reason = "cancelled" } });
            CancelIncoming(inc);
        }
    }

    private void HandleCancel(PairedDevice from, string transferId)
    {
        if (_outgoing.TryGetValue(transferId, out var o) && o.Peer == from.Fingerprint)
        {
            o.Result.TrySetResult(new TransferResult.Cancelled());
            o.Cancel.Cancel();
        }
        if (_incoming.TryGetValue(transferId, out var inc) && inc.From.Fingerprint == from.Fingerprint) CancelIncoming(inc);
    }

    private void CancelIncoming(IncomingTransfer t)
    {
        if (!_incoming.TryRemove(new KeyValuePair<string, IncomingTransfer>(t.Id, t))) return;
        DiscardUnfinished(t);
        _events.OnTransferFinished(t.Id, t.From, new TransferResult.Cancelled());
    }

    /// <summary>Reports progress across the whole transfer, not per file: bytes of earlier files count as done.</summary>
    private TransferEngine.Progress ProgressFor(string id, TransferOffer offer, int index)
    {
        long before = 0, all = 0;
        for (int i = 0; i < offer.Files.Count; i++)
        {
            long size = checked((long)offer.Files[i].Size);
            all = checked(all + size);
            if (i < index) before = checked(before + size);
        }
        return (done, _) => _events.OnTransferProgress(id, before + done, all);
    }

    /// <summary>A fresh transfer id, for callers that want to track a send before it starts.</summary>
    public static string NewTransferId() => Hex.Encode(RandomNumberGenerator.GetBytes(Limits.TransferIdHexChars / 2));

    // ---------------------------------------------------------------------------------------------

    private Hello MyHello()
    {
        var h = new Hello { DeviceId = _identity.DeviceId, DeviceName = _deviceName, Kind = _kind, ProtocolVersion = Limits.ProtocolVersion };
        h.Capabilities.Add(AllCapabilities());
        return h;
    }

    private static void ValidateHello(Hello hello, Fingerprint peerFp)
    {
        if (hello.DeviceId != peerFp.DeviceId) throw new ProtocolException("device id does not match certificate");
        if (hello.ProtocolVersion < 1) throw new ProtocolException("unsupported protocol version");
    }

    private static async Task WriteNowAsync(Stream s, Envelope env, CancellationToken ct)
    {
        await Envelopes.WriteAsync(s, env, Limits.MaxHandshakeFrameBytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Disconnects from a peer and forgets it.</summary>
    public void Unpair(Fingerprint fp)
    {
        _trust.Remove(fp);
        if (_sessions.TryGetValue(fp, out var s)) s.Close(new IOException("unpaired"));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var s in _sessions.Values) s.Close();
        _sessions.Clear();
        foreach (var t in _outgoing.Values) t.Result.TrySetResult(new TransferResult.Failed("shutting down"));
        _slots.Dispose();
        _cts.Dispose();
    }

    private static string SanitizeName(string? name)
    {
        string s = new string((name ?? "").Where(c => c >= 0x20 && c != 0x7F).ToArray()).Trim();
        if (s.Length > Limits.MaxDeviceNameChars) s = s[..Limits.MaxDeviceNameChars];
        return s.Length == 0 ? "Device" : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed class PairingWindow(byte[] secret, long expiresAt)
    {
        public byte[] Secret { get; } = secret;
        public bool IsOpen(long now) => now < expiresAt;
    }
}
