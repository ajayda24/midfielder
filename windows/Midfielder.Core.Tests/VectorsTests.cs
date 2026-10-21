using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Midfielder.Core.Crypto;
using Midfielder.Core.Discovery;
using Midfielder.Core.Transfer;
using Midfielder.Core.Wire;
using Midfielder.Protocol;
using Xunit;

namespace Midfielder.Core.Tests;

/// <summary>Golden vectors shared with the Kotlin suite (protocol/test-vectors/vectors.properties).</summary>
public class VectorsTests
{
    private static readonly Dictionary<string, string> V = Load();

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "vectors.properties"), Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int i = IndexOfUnescaped(line, '=');
            map[Unescape(line[..i])] = Unescape(line[(i + 1)..]);
        }
        return map;
    }

    private static int IndexOfUnescaped(string s, char c)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == c) return i;
        }
        throw new FormatException(s);
    }

    /// <summary>The subset of java.util.Properties escaping the vector file uses.</summary>
    private static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            char n = s[++i];
            if (n == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
            else sb.Append(n);
        }
        return sb.ToString();
    }

    private static async Task<string> FrameHex(Envelope env)
    {
        using var ms = new MemoryStream();
        await Envelopes.WriteAsync(ms, env, Limits.MaxFrameBytes, CancellationToken.None);
        return Hex.Encode(ms.ToArray());
    }

    [Fact]
    public async Task HelloFrame()
    {
        var hello = new Hello { DeviceId = "00112233445566778899aabbccddeeff", DeviceName = "Laptop", Kind = DeviceKind.Windows, ProtocolVersion = 1 };
        hello.Capabilities.Add(["clipboard.text", "file"]);
        var env = new Envelope { Hello = hello };
        Assert.Equal(V["frame.hello"], await FrameHex(env));
        var decoded = await Envelopes.ReadAsync(new MemoryStream(Hex.Decode(V["frame.hello"])), 1024, CancellationToken.None);
        Assert.Equal(env, decoded);
    }

    [Fact]
    public async Task ClipboardFrame()
    {
        byte[] text = Encoding.UTF8.GetBytes("héllo 👋");
        var env = new Envelope
        {
            Clipboard = new ClipboardItem
            {
                Id = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()),
                OriginDeviceId = "00112233445566778899aabbccddeeff",
                CreatedAtMs = 1_700_000_000_000,
                Kind = ClipKind.Text,
                Mime = "text/plain",
                ContentSha256 = ByteString.CopyFrom(SHA256.HashData(text)),
                InlineData = ByteString.CopyFrom(text),
                Size = (ulong)text.Length,
            },
        };
        Assert.Equal(V["frame.clipboard"], await FrameHex(env));
    }

    [Fact]
    public async Task FeatureFrames()
    {
        var battery = new Envelope { BatteryStatus = new BatteryStatus { Level = 64, Charging = true, Plugged = true } };
        Assert.Equal(V["frame.battery"], await FrameHex(battery));
        var action = new Envelope { NotificationAction = new NotificationAction { Key = "0|com.whatsapp|1|null|10001", ReplyText = "On my way 👍" } };
        Assert.Equal(V["frame.notification_action"], await FrameHex(action));
        var decoded = await Envelopes.ReadAsync(new MemoryStream(Hex.Decode(V["frame.notification_action"])), 1024, CancellationToken.None);
        Assert.Equal(action, decoded);
        var seek = new Envelope { MediaCommand = new MediaCommand { Action = MediaAction.Seek, SeekMs = 74_000 } };
        Assert.Equal(V["frame.media_command"], await FrameHex(seek));
        var offer = new Envelope { AudioOffer = new AudioOffer { StreamId = "00112233445566778899aabbccddeeff", SampleRate = 48_000, Channels = 2 } };
        Assert.Equal(V["frame.audio_offer"], await FrameHex(offer));
        var chunk = new Envelope { AudioChunk = new AudioChunk { Seq = 7, Pcm = ByteString.CopyFrom(0, 1, 0xff, 0x7f) } };
        Assert.Equal(V["frame.audio_chunk"], await FrameHex(chunk));
    }

    [Fact]
    public void PairingProofs()
    {
        byte[] secret = Hex.Decode(V["pair.secret"]);
        var c = new Fingerprint(V["pair.client_fp"]);
        var s = new Fingerprint(V["pair.server_fp"]);
        Assert.Equal(V["pair.client_proof"], Hex.Encode(PairingProof.Client(secret, c, s)));
        Assert.Equal(V["pair.server_proof"], Hex.Encode(PairingProof.Server(secret, c, s)));
    }

    [Fact]
    public void KnockBytes() => Assert.Equal(V["knock"], Hex.Encode(new Knock("00112233445566778899aabbccddeeff", 47811).Encode()));

    [Fact]
    public void FileNameSanitizer_MatchesKotlin()
    {
        int i = 0;
        while (V.TryGetValue($"sanitize.{i}.in", out string? input))
        {
            Assert.Equal(V[$"sanitize.{i}.out"], FileNameSanitizer.Sanitize(input));
            i++;
        }
        Assert.True(i > 5);
    }
}
