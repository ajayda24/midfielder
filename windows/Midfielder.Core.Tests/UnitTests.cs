using System.Text;
using Midfielder.Core.Clip;
using Midfielder.Core.Crypto;
using Midfielder.Core.Discovery;
using Midfielder.Core.Transfer;
using Midfielder.Core.Wire;
using Midfielder.Protocol;
using Xunit;

namespace Midfielder.Core.Tests;

public class FramingTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0, 0, 4, 1 })]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    public async Task RejectsBadLengthsBeforeAllocating(byte[] header) =>
        await Assert.ThrowsAsync<ProtocolException>(async () => await Envelopes.ReadAsync(new MemoryStream(header), 1024, CancellationToken.None));

    [Fact]
    public async Task TruncatedFrameIsEndOfStream() =>
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await Envelopes.ReadAsync(new MemoryStream([0, 0, 0, 5, 1]), 1024, CancellationToken.None));

    [Fact]
    public async Task CleanEofReturnsNull() =>
        Assert.Null(await Envelopes.ReadAsync(new MemoryStream(), 1024, CancellationToken.None));

    [Fact]
    public async Task RandomGarbageOnlyRaisesProtocolOrEof()
    {
        var rnd = new Random(1234);
        for (int i = 0; i < 20_000; i++)
        {
            byte[] data = new byte[rnd.Next(64)];
            rnd.NextBytes(data);
            try
            {
                await Envelopes.ReadAsync(new MemoryStream(data), 4096, CancellationToken.None);
            }
            catch (ProtocolException) { }
            catch (EndOfStreamException) { }
        }
    }

    [Fact]
    public void InviteRoundTripAndKotlinStyleDecoding()
    {
        using var id = DeviceIdentity.Generate();
        var invite = new PairingInvite(id.Fingerprint, PairingInvite.NewSecret(), [new HostPort("192.168.1.20", 47810), new HostPort("fe80::1", 47810)], "Ajay's laptop & co");
        var parsed = PairingInvite.Parse(invite.ToUri());
        Assert.Equal(invite.Fingerprint, parsed.Fingerprint);
        Assert.Equal(invite.Secret, parsed.Secret);
        Assert.Equal(invite.Addresses, parsed.Addresses);
        Assert.Equal(invite.Name, parsed.Name);
        // Kotlin's URLEncoder writes spaces as '+'.
        var kotlinStyle = invite.ToUri().Replace("%20", "+", StringComparison.Ordinal);
        Assert.Equal(invite.Name, PairingInvite.Parse(kotlinStyle).Name);
        Assert.Throws<ArgumentException>(() => PairingInvite.Parse("https://evil.example/pair?v=1"));
    }

    [Fact]
    public void IdentityRoundTrip()
    {
        using var id = DeviceIdentity.Generate();
        using var restored = DeviceIdentity.FromPkcs12(id.ToPkcs12("pw"), "pw");
        Assert.Equal(id.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public void KnockAndBackoff()
    {
        var k = new Knock("00112233445566778899aabbccddeeff", 47810);
        Assert.Equal(k, Knock.Decode(k.Encode()));
        Assert.Null(Knock.Decode(new byte[22]));
        var b = new Backoff(100, 1000, jitter: 0);
        Assert.Equal([100L, 200, 400, 800, 1000, 1000], Enumerable.Range(0, 6).Select(_ => b.NextDelayMs()));
    }
}

public class ClipSyncTests
{
    private sealed class Rig : IDisposable
    {
        public readonly List<ClipboardItem> Sent = [];
        public readonly List<ClipContent> Applied = [];
        public long Now;
        public readonly ClipSync Sync;

        public Rig(string id, long now = 1000)
        {
            Now = now;
            Sync = new ClipSync(id, item => { lock (Sent) Sent.Add(item); return ValueTask.CompletedTask; },
                (c, _) => { Applied.Add(c); return ValueTask.CompletedTask; }, () => Now);
        }

        public ClipboardItem LastSent()
        {
            for (int i = 0; i < 200; i++)
            {
                lock (Sent) if (Sent.Count > 0) return Sent[^1];
                Thread.Sleep(5);
            }
            throw new TimeoutException();
        }

        public void Dispose() => Sync.Dispose();
    }

    [Fact]
    public async Task NoEchoAndDedupe()
    {
        using var pc = new Rig("pc");
        using var phone = new Rig("phone");
        Assert.True(pc.Sync.OnLocalChange(new TextClip("hello")));
        var item = pc.LastSent();
        Assert.True(await phone.Sync.OnRemoteAsync(item));
        Assert.Equal("hello", ((TextClip)phone.Applied.Single()).Text);
        Assert.False(phone.Sync.OnLocalChange(new TextClip("hello")));
        Assert.False(await phone.Sync.OnRemoteAsync(item));
        Assert.False(pc.Sync.OnLocalChange(new TextClip("hello")));
    }

    [Fact]
    public async Task TamperedRejectedAndCatchUpNewerOnly()
    {
        using var pc = new Rig("pc", 1000);
        using var phone = new Rig("phone", 2000);
        pc.Sync.OnLocalChange(new TextClip("old"));
        var item = pc.LastSent();
        var tampered = item.Clone();
        tampered.InlineData = Google.Protobuf.ByteString.CopyFrom(Encoding.UTF8.GetBytes("bad"));
        tampered.Size = 3;
        Assert.False(await phone.Sync.OnRemoteAsync(tampered));
        phone.Sync.OnLocalChange(new TextClip("newer"));
        Assert.False(await phone.Sync.OnRemoteAsync(pc.Sync.CatchUpItem()!));
        Assert.True(await pc.Sync.OnRemoteAsync(phone.Sync.CatchUpItem()!));
    }

    [Fact]
    public void HistoryBounds()
    {
        var h = new ClipboardHistory(maxItems: 3, maxTotalBytes: 10);
        foreach (string s in new[] { "aa", "bb", "cc", "dd" }) h.Add(new ClipboardHistory.Entry(new TextClip(s), "x", 0));
        Assert.Equal(["dd", "cc", "bb"], h.Snapshot().Select(e => ((TextClip)e.Content).Text));
        h.Add(new ClipboardHistory.Entry(new TextClip("secret", sensitive: true), "x", 0));
        Assert.Equal(3, h.Count);
        h.Add(new ClipboardHistory.Entry(new TextClip("123456789"), "x", 0));
        Assert.True(h.TotalBytes <= 10);
    }
}

public class TransferEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-test").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static byte[] Data(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    /// <summary>A connected pair of in-memory duplex streams (via a loopback socket pair).</summary>
    private static async Task<(Stream, Stream, IDisposable)> PipeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var client = new System.Net.Sockets.TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        var server = await accept;
        listener.Stop();
        return (client.GetStream(), server.GetStream(), new Both(client, server));
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private static async Task<string> Transfer(byte[] bytes, FileEntry entry, IFileSink sink)
    {
        var (a, b, pair) = await PipeAsync();
        using (pair)
        {
            var send = TransferEngine.SendAsync(a, new BytesFileSource(entry.Name, bytes, "x"), entry, null, 10_000, CancellationToken.None);
            string location;
            try
            {
                location = await TransferEngine.ReceiveAsync(b, sink, entry, null, 10_000, CancellationToken.None);
            }
            catch
            {
                // Unblock the sender, then let it finish (its own failure is expected here).
                pair.Dispose();
                try { await send; } catch (Exception) { }
                throw;
            }
            await send; // success: the sender must also see the receiver's confirmation
            return location;
        }
    }

    [Fact]
    public async Task TransfersAndResumes()
    {
        byte[] bytes = Data(1_000_003);
        var entry = new FileEntry { Name = "big.bin", Size = (ulong)bytes.Length };
        string loc = await Transfer(bytes, entry, new DirectoryFileSink(_dir, "t1", "big.bin"));
        Assert.Equal(bytes, File.ReadAllBytes(loc));

        var sink = new DirectoryFileSink(_dir, "t2", "resume.bin");
        using (var s = sink.OpenAt(0)) s.Write(bytes, 0, 400_000);
        loc = await Transfer(bytes, entry, sink);
        Assert.Equal(bytes, File.ReadAllBytes(loc));
    }

    [Fact]
    public async Task CorruptPartialIsDiscarded()
    {
        byte[] bytes = Data(50_000);
        var entry = new FileEntry { Name = "c.bin", Size = (ulong)bytes.Length };
        var sink = new DirectoryFileSink(_dir, "t3", "c.bin");
        using (var s = sink.OpenAt(0)) s.Write(new byte[10_000]);
        await Assert.ThrowsAsync<TransferFailedException>(() => Transfer(bytes, entry, sink));
        Assert.Equal(0, sink.PartialLength());
        Assert.Equal(bytes, File.ReadAllBytes(await Transfer(bytes, entry, sink)));
    }

    [Fact]
    public async Task NeverOverwrites()
    {
        File.WriteAllBytes(Path.Combine(_dir, "same.txt"), [1]);
        string loc = await Transfer(Data(10), new FileEntry { Name = "same.txt", Size = 10 }, new DirectoryFileSink(_dir, "t5", "same.txt"));
        Assert.Equal("same (1).txt", Path.GetFileName(loc));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(_dir, "same.txt")));
    }
}
