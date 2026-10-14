using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Midfielder.Core.Crypto;

public sealed record HostPort
{
    public HostPort(string host, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("port out of range", nameof(port));
        if (string.IsNullOrEmpty(host) || host.Length > 253) throw new ArgumentException("bad host", nameof(host));
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    public override string ToString() => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static HostPort Parse(string s)
    {
        string host, port;
        if (s.StartsWith('['))
        {
            int end = s.IndexOf("]:", StringComparison.Ordinal);
            if (end <= 1) throw new ArgumentException("bad IPv6 address");
            host = s[1..end];
            port = s[(end + 2)..];
        }
        else
        {
            int idx = s.LastIndexOf(':');
            if (idx <= 0) throw new ArgumentException("missing port");
            host = s[..idx];
            port = s[(idx + 1)..];
        }
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int p)) throw new ArgumentException("bad port");
        return new HostPort(host, p);
    }
}

/// <summary>
/// The QR payload shown by the PC: <c>midfielder://pair?v=1&amp;fp=..&amp;s=..&amp;a=..&amp;n=..</c>
/// (format identical to the Kotlin implementation).
/// </summary>
public sealed class PairingInvite
{
    public const int SecretBytes = 32;
    public const int MaxAddresses = 8;
    private const int MaxUriChars = 2048;
    private const string Prefix = "midfielder://pair?";

    public PairingInvite(Fingerprint fingerprint, byte[] secret, IReadOnlyList<HostPort> addresses, string name)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(addresses);
        if (secret.Length != SecretBytes) throw new ArgumentException($"secret must be {SecretBytes} bytes", nameof(secret));
        if (addresses.Count is < 1 or > MaxAddresses) throw new ArgumentException($"1..{MaxAddresses} addresses required", nameof(addresses));
        Fingerprint = fingerprint;
        Secret = secret;
        Addresses = addresses;
        Name = name;
    }

    public Fingerprint Fingerprint { get; }
#pragma warning disable CA1819 // a fixed 32-byte secret; copying it on each access would only add garbage.
    public byte[] Secret { get; }
#pragma warning restore CA1819
    public IReadOnlyList<HostPort> Addresses { get; }
    public string Name { get; }

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public string ToUri() =>
        $"{Prefix}v=1&fp={Fingerprint.Value}&s={Base64Url(Secret)}" +
        $"&a={Uri.EscapeDataString(string.Join(',', Addresses))}&n={Uri.EscapeDataString(Name)}";

    public static PairingInvite Parse(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Length > MaxUriChars) throw new ArgumentException("invite too long");
        if (!uri.StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException("not a midfielder pairing code");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string kv in uri[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=', StringComparison.Ordinal);
            if (i <= 0) throw new ArgumentException("bad query");
            query[kv[..i]] = WebUtility.UrlDecode(kv[(i + 1)..]);
        }
        if (!query.TryGetValue("v", out string? v) || v != "1") throw new ArgumentException("unsupported invite version");
        var fp = new Fingerprint(query.GetValueOrDefault("fp") ?? throw new ArgumentException("missing fp"));
        byte[] secret = FromBase64Url(query.GetValueOrDefault("s") ?? throw new ArgumentException("missing s"));
        var addresses = (query.GetValueOrDefault("a") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(HostPort.Parse).ToList();
        string name = query.GetValueOrDefault("n") ?? "";
        if (name.Length > Limits.MaxDeviceNameChars) name = name[..Limits.MaxDeviceNameChars];
        return new PairingInvite(fp, secret, addresses, name);
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + ((4 - (b.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(b);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("bad secret", e);
        }
    }
}

/// <summary>HMAC proofs that both sides know the invite secret, bound to both certificates.</summary>
public static class PairingProof
{
    private static readonly byte[] ClientLabel = Encoding.ASCII.GetBytes("midfielder-pair-client-v1");
    private static readonly byte[] ServerLabel = Encoding.ASCII.GetBytes("midfielder-pair-server-v1");

    public static byte[] Client(byte[] secret, Fingerprint clientFp, Fingerprint serverFp) => Mac(secret, ClientLabel, clientFp, serverFp);

    public static byte[] Server(byte[] secret, Fingerprint clientFp, Fingerprint serverFp) => Mac(secret, ServerLabel, clientFp, serverFp);

    private static byte[] Mac(byte[] secret, byte[] label, Fingerprint a, Fingerprint b)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        h.AppendData(label);
        h.AppendData(a.Bytes);
        h.AppendData(b.Bytes);
        return h.GetHashAndReset();
    }
}
