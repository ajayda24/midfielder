using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Midfielder.Core.Crypto;

namespace Midfielder.Core.Net;

/// <summary>TLS 1.3 with mutual authentication; trust is fingerprint pinning only (no CAs, no hostnames).</summary>
internal static class Tls
{
    public static SslServerAuthenticationOptions ServerOptions(DeviceIdentity identity, Func<Fingerprint, bool> accept) => new()
    {
        ServerCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, additionalCertificates: null, offline: true),
        ClientCertificateRequired = true,
        EnabledSslProtocols = SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && accept(Fingerprint.Of(cert)),
    };

    public static SslClientAuthenticationOptions ClientOptions(DeviceIdentity identity, string host, Fingerprint expected) => new()
    {
        TargetHost = host,
        ClientCertificates = new X509CertificateCollection { identity.Certificate },
        LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
        EnabledSslProtocols = SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        // Self-signed certs always produce chain errors; the pinned fingerprint is the only trust decision.
        RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Fingerprint.Of(cert) == expected,
    };
}
