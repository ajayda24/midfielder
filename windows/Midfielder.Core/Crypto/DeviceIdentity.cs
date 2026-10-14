using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Midfielder.Core.Crypto;

/// <summary>
/// This device's long-term identity: an EC P-256 key with a self-signed certificate. Peers pin its
/// <see cref="Fingerprint"/>; there is no CA.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private DeviceIdentity(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey) throw new ArgumentException("certificate has no private key", nameof(certificate));
        Certificate = certificate;
        Fingerprint = Fingerprint.Of(certificate);
    }

    public X509Certificate2 Certificate { get; }
    public Fingerprint Fingerprint { get; }
    public string DeviceId => Fingerprint.DeviceId;

    public static DeviceIdentity Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=midfielder", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        // Round-trip through PKCS#12: Windows' TLS stack (SChannel) can't use the ephemeral key directly.
        return Load(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Serializes to PKCS#12. The app protects the result with DPAPI.</summary>
    public byte[] ToPkcs12(string? password) => Certificate.Export(X509ContentType.Pkcs12, password);

    public static DeviceIdentity FromPkcs12(byte[] pkcs12, string? password) => Load(pkcs12, password);

    private static DeviceIdentity Load(byte[] pkcs12, string? password)
    {
        var flags = X509KeyStorageFlags.Exportable;
        if (OperatingSystem.IsWindows()) flags |= X509KeyStorageFlags.UserKeySet;
#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+; this target is .NET 8.
        return new DeviceIdentity(new X509Certificate2(pkcs12, password, flags));
#pragma warning restore SYSLIB0057
    }

    public void Dispose() => Certificate.Dispose();
}
