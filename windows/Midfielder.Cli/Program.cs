using System.Net;
using System.Text;
using Google.Protobuf;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Core.Transfer;
using Midfielder.Protocol;

// Headless Midfielder node. Used for debugging and by the Kotlin cross-language interop test.
//
//   midfielder-cli listen [--port N] [--bind ADDR] [--dir DIR]
//
// Prints one line per event ("INVITE <uri>", "PAIRED <name>", "CLIP <text>", "RECEIVED <path>", ...).
// Test behaviors: echoes each text clip back as "echo: <text>", and sends each received file back as "back-<name>".
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

if (args.Length == 0 || args[0] != "listen")
{
    Console.Error.WriteLine("usage: midfielder-cli listen [--port N] [--bind ADDR] [--dir DIR]");
    return 2;
}

int port = int.Parse(Arg("--port") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var bind = IPAddress.Parse(Arg("--bind") ?? "127.0.0.1");
string dir = Directory.CreateDirectory(Arg("--dir") ?? Path.Combine(Path.GetTempPath(), "midfielder-cli")).FullName;

using var identity = DeviceIdentity.Generate();
var events = new CliEvents(dir);
await using var node = new MidfielderNode(identity, "cli-pc", DeviceKind.Windows, new InMemoryTrustStore(), events);
events.Node = node;
int actualPort = node.StartListening(port, bind);
var invite = node.OpenPairing([new HostPort(bind.ToString(), actualPort)]);
Console.WriteLine($"INVITE {invite.ToUri()}");

// Run until stdin closes (the test harness closes it to stop us).
await Task.Run(() => { while (Console.ReadLine() is not null) { } });
return 0;

internal sealed class CliEvents(string dir) : INodeEvents
{
    public MidfielderNode? Node { get; set; }

    public void OnPaired(PairedDevice device) => Console.WriteLine($"PAIRED {device.Name}");

    public void OnPeerConnected(PeerSession session) => Console.WriteLine($"CONNECTED {session.Peer.Name}");

    public ValueTask OnClipboardAsync(PairedDevice from, ClipboardItem item)
    {
        string text = item.InlineData.ToStringUtf8();
        Console.WriteLine($"CLIP {text}");
        if (!text.StartsWith("echo: ", StringComparison.Ordinal))
        {
            byte[] bytes = Encoding.UTF8.GetBytes("echo: " + text);
            Node?.BroadcastClipboard(new ClipboardItem
            {
                Kind = ClipKind.Text, Mime = "text/plain", InlineData = ByteString.CopyFrom(bytes), Size = (ulong)bytes.Length,
                ContentSha256 = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(bytes)),
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<IFileSink>?> OnTransferOfferAsync(PairedDevice from, TransferOffer offer, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<IFileSink>?>(offer.Files.Select((f, i) => (IFileSink)new DirectoryFileSink(dir, $"{offer.TransferId}-{i}", f.Name)).ToList());

    public void OnTransferFinished(string transferId, PairedDevice from, TransferResult result)
    {
        if (result is not TransferResult.Completed done)
        {
            Console.WriteLine($"TRANSFER_FAILED {result}");
            return;
        }
        foreach (string path in done.Locations)
        {
            Console.WriteLine($"RECEIVED {path}");
            var back = new BytesFileSource("back-" + Path.GetFileName(path), File.ReadAllBytes(path), "application/octet-stream");
            _ = Task.Run(async () =>
            {
                var r = await Node!.SendFilesAsync(from.Fingerprint, [back]);
                Console.WriteLine($"SENT_BACK {r.GetType().Name}");
            });
        }
    }
}
