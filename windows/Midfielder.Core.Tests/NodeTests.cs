using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Core.Transfer;
using Midfielder.Protocol;
using Xunit;

namespace Midfielder.Core.Tests;

/// <summary>Two real nodes over TLS 1.3 on loopback (same scenarios as the Kotlin NodeIntegrationTest).</summary>
public sealed class NodeTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-node").FullName;
    private readonly List<Device> _devices = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _devices) await d.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder(string inbox) : INodeEvents
    {
        public bool Accept { get; set; } = true;
        public Channel<ClipboardItem> Clips { get; } = Channel.CreateUnbounded<ClipboardItem>();
        public Channel<TransferResult> Finished { get; } = Channel.CreateUnbounded<TransferResult>();
        public Channel<PairedDevice> Paired { get; } = Channel.CreateUnbounded<PairedDevice>();
        public Channel<Exception?> Disconnected { get; } = Channel.CreateUnbounded<Exception?>();

        public void OnPaired(PairedDevice device) => Paired.Writer.TryWrite(device);
        public void OnPeerDisconnected(PairedDevice device, Exception? cause) => Disconnected.Writer.TryWrite(cause);
        public ValueTask OnClipboardAsync(PairedDevice from, ClipboardItem item) => Clips.Writer.WriteAsync(item);
        public ValueTask<IReadOnlyList<IFileSink>?> OnTransferOfferAsync(PairedDevice from, TransferOffer offer, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<IFileSink>?>(Accept
                ? offer.Files.Select((f, i) => (IFileSink)new DirectoryFileSink(inbox, $"{offer.TransferId}-{i}", f.Name)).ToList()
                : null);
        public void OnTransferFinished(string transferId, PairedDevice from, TransferResult result) => Finished.Writer.TryWrite(result);
    }

    private sealed class Device : IAsyncDisposable
    {
        public Device(string root, string name, DeviceKind kind, NodeConfig? config = null)
        {
            Inbox = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            Events = new Recorder(Inbox);
            Identity = DeviceIdentity.Generate();
            Node = new MidfielderNode(Identity, name, kind, Trust, Events, config);
        }

        public string Inbox { get; }
        public Recorder Events { get; }
        public InMemoryTrustStore Trust { get; } = new();
        public DeviceIdentity Identity { get; }
        public MidfielderNode Node { get; }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            Identity.Dispose();
        }
    }

    private (Device Pc, Device Phone, HostPort Addr) PcAndPhone(NodeConfig? pcConfig = null, NodeConfig? phoneConfig = null)
    {
        var pc = new Device(_dir, "pc", DeviceKind.Windows, pcConfig);
        var phone = new Device(_dir, "phone", DeviceKind.Android, phoneConfig);
        _devices.Add(pc);
        _devices.Add(phone);
        int port = pc.Node.StartListening(0, IPAddress.Loopback);
        return (pc, phone, new HostPort("127.0.0.1", port));
    }

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(20));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private static byte[] Data(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task PairsOnceThenTrustsBothWays()
    {
        var (pc, phone, addr) = PcAndPhone();
        var invite = pc.Node.OpenPairing([addr]);
        var session = await Within(phone.Node.PairAsync(PairingInvite.Parse(invite.ToUri())));
        Assert.True(session.IsOpen);
        Assert.Equal("pc", session.Peer.Name);
        Assert.Equal("phone", (await Within(pc.Events.Paired.Reader.ReadAsync().AsTask())).Name);
        Assert.True(pc.Trust.IsTrusted(phone.Node.Fingerprint));

        var intruder = new Device(_dir, "intruder", DeviceKind.Android);
        _devices.Add(intruder);
        await Assert.ThrowsAsync<IOException>(() => Within(intruder.Node.PairAsync(invite)));
        Assert.False(pc.Trust.IsTrusted(intruder.Node.Fingerprint));
    }

    [Fact]
    public async Task WrongCodeBurnsTheWindow()
    {
        var (pc, phone, addr) = PcAndPhone();
        var invite = pc.Node.OpenPairing([addr]);
        var forged = new PairingInvite(invite.Fingerprint, PairingInvite.NewSecret(), invite.Addresses, invite.Name);
        await Assert.ThrowsAsync<IOException>(() => Within(phone.Node.PairAsync(forged)));
        await Assert.ThrowsAsync<IOException>(() => Within(phone.Node.PairAsync(invite)));
        Assert.Empty(pc.Trust.All());
    }

    [Fact]
    public async Task UnpairedDevicesCannotConnect()
    {
        var (pc, _, addr) = PcAndPhone();
        var stranger = new Device(_dir, "stranger", DeviceKind.Android);
        _devices.Add(stranger);
        var fakePc = new PairedDevice(pc.Node.Fingerprint, "pc", DeviceKind.Windows);
        stranger.Trust.Put(fakePc);
        await Assert.ThrowsAnyAsync<Exception>(() => Within(stranger.Node.ConnectAsync(fakePc, addr)));
        Assert.Empty(pc.Node.ConnectedPeers);
    }

    [Fact]
    public async Task ClipboardBothWays()
    {
        var (pc, phone, addr) = PcAndPhone();
        await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Until(() => pc.Node.ConnectedPeers.Count == 1);
        phone.Node.BroadcastClipboard(Clip("from phone"));
        Assert.Equal("from phone", (await Within(pc.Events.Clips.Reader.ReadAsync().AsTask())).InlineData.ToStringUtf8());
        pc.Node.BroadcastClipboard(Clip("from pc"));
        Assert.Equal("from pc", (await Within(phone.Events.Clips.Reader.ReadAsync().AsTask())).InlineData.ToStringUtf8());
    }

    [Fact]
    public async Task FilesBothDirections()
    {
        var (pc, phone, addr) = PcAndPhone();
        await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Until(() => pc.Node.ConnectedPeers.Count == 1);

        byte[] photo = Data(2_500_000);
        var up = await Within(phone.Node.SendFilesAsync(pc.Node.Fingerprint, [new BytesFileSource("IMG_0001.jpg", photo, "image/jpeg")]));
        Assert.IsType<TransferResult.Completed>(up);
        var received = Assert.IsType<TransferResult.Completed>(await Within(pc.Events.Finished.Reader.ReadAsync().AsTask()));
        Assert.Equal(photo, File.ReadAllBytes(received.Locations.Single()));

        byte[] doc = Data(700_001);
        var down = await Within(pc.Node.SendFilesAsync(phone.Node.Fingerprint, [new BytesFileSource("report.pdf", doc, "application/pdf")]));
        Assert.IsType<TransferResult.Completed>(down);
        var got = Assert.IsType<TransferResult.Completed>(await Within(phone.Events.Finished.Reader.ReadAsync().AsTask()));
        Assert.Equal(doc, File.ReadAllBytes(got.Locations.Single()));
    }

    [Fact]
    public async Task DeclinedOfferFails()
    {
        var (pc, phone, addr) = PcAndPhone();
        await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Until(() => pc.Node.ConnectedPeers.Count == 1);
        pc.Events.Accept = false;
        var result = await Within(phone.Node.SendFilesAsync(pc.Node.Fingerprint, [new BytesFileSource("a", new byte[5], "x")]));
        Assert.IsType<TransferResult.Failed>(result);
    }

    [Fact]
    public async Task InterruptedTransferResumes()
    {
        var (pc, phone, addr) = PcAndPhone();
        await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Until(() => pc.Node.ConnectedPeers.Count == 1);
        byte[] video = Data(3_000_000);
        var source = new FlakySource("clip.mp4", video, failAfter: 1_200_000);
        var result = await Within(phone.Node.SendFilesAsync(pc.Node.Fingerprint, [source]));
        Assert.IsType<TransferResult.Completed>(result);
        var received = Assert.IsType<TransferResult.Completed>(await Within(pc.Events.Finished.Reader.ReadAsync().AsTask()));
        Assert.Equal(video, File.ReadAllBytes(received.Locations.Single()));
        Assert.Equal(2, source.Opens);
    }

    [Fact]
    public async Task SilentPeerIsDropped()
    {
        var (pc, phone, addr) = PcAndPhone(new NodeConfig { ListenerIdleTimeoutMs = 600 }, new NodeConfig { HeartbeatIntervalMs = 60_000 });
        await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Within(phone.Events.Disconnected.Reader.ReadAsync().AsTask());
        await Until(() => pc.Node.ConnectedPeers.Count == 0);
    }

    [Fact]
    public async Task HeartbeatKeepsSessionAlive()
    {
        var (pc, phone, addr) = PcAndPhone(new NodeConfig { ListenerIdleTimeoutMs = 1_500 }, new NodeConfig { HeartbeatIntervalMs = 300, HeartbeatTimeoutMs = 300 });
        var session = await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        await Task.Delay(3_000);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public async Task ReconnectsAndUnpairCutsOff()
    {
        var (pc, phone, addr) = PcAndPhone();
        var first = await Within(phone.Node.PairAsync(pc.Node.OpenPairing([addr])));
        var pcDevice = first.Peer;
        first.Close();
        await Until(() => pc.Node.ConnectedPeers.Count == 0);
        var second = await Within(phone.Node.ConnectAsync(pcDevice, addr));
        await Until(() => pc.Node.ConnectedPeers.Count == 1);
        pc.Node.Unpair(phone.Node.Fingerprint);
        await Until(() => !second.IsOpen);
        await Assert.ThrowsAnyAsync<Exception>(() => Within(phone.Node.ConnectAsync(pcDevice, addr)));
    }

    private static ClipboardItem Clip(string text)
    {
        var bytes = ByteString.CopyFromUtf8(text);
        return new ClipboardItem
        {
            Kind = ClipKind.Text, Mime = "text/plain", InlineData = bytes, Size = (ulong)bytes.Length,
            ContentSha256 = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(bytes.Span)),
        };
    }

    private sealed class FlakySource(string name, byte[] bytes, int failAfter) : IFileSource
    {
        private int _opens;
        public int Opens => _opens;
        public string Name { get; } = name;
        public long Size => bytes.Length;
        public string Mime => "video/mp4";
        public long ModifiedMs => 0;

        public Stream Open()
        {
            int attempt = Interlocked.Increment(ref _opens);
            return new Flaky(bytes, attempt == 1 ? failAfter : int.MaxValue);
        }

        private sealed class Flaky(byte[] data, int failAfter) : MemoryStream(data, writable: false)
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= failAfter) throw new IOException("wifi dropped");
                return base.Read(buffer, offset, (int)Math.Min(count, failAfter - Position));
            }

            public override int Read(Span<byte> buffer)
            {
                if (Position >= failAfter) throw new IOException("wifi dropped");
                return base.Read(buffer[..(int)Math.Min(buffer.Length, failAfter - Position)]);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(Read(buffer.Span));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer, offset, count));
        }
    }
}
