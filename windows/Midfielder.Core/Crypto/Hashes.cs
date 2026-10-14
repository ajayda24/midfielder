using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Midfielder.Core.Crypto;

public static class Hex
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static byte[] Decode(string hex) => Convert.FromHexString(hex);

    public static bool IsLowerHex(string? s, int length)
    {
        if (s is null || s.Length != length) return false;
        foreach (char c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}

/// <summary>SHA-256 of a DER certificate, lowercase hex. This is what devices pin.</summary>
public readonly record struct Fingerprint
{
    public Fingerprint(string hex)
    {
        if (!Hex.IsLowerHex(hex, 64)) throw new ArgumentException("fingerprint must be 64 lowercase hex chars", nameof(hex));
        Value = hex;
    }

    public string Value { get; }

    public byte[] Bytes => Hex.Decode(Value);

    /// <summary>Short, stable device id: first 16 bytes of the fingerprint.</summary>
    public string DeviceId => Value[..32];

    public string Display()
    {
        string v = Value;
        return $"{v[..4]} {v[4..8]} {v[8..12]} {v[12..16]}";
    }

    public override string ToString() => Value;

    public static Fingerprint Of(X509Certificate cert) => new(Hex.Encode(SHA256.HashData(cert.GetRawCertData())));
}
