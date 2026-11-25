using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Passload.Core;

internal static class LocalIdentity
{
    public static X509Certificate2 Create(IEnumerable<IPAddress> addresses)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Passload", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        foreach (var address in addresses.Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct())
            names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddYears(1));
        // Schannel uses a current-user key container, released with the certificate.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
    }

    public static (X509Certificate2 Certificate, X509Certificate2Collection Intermediates) Trusted(
        string host, DateTimeOffset validThrough)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var candidates = store.Certificates;
        try
        {
            foreach (var certificate in candidates.OrderByDescending(x => x.NotAfter))
            {
                if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                    certificate.NotAfter.ToUniversalTime() <= validThrough.UtcDateTime ||
                    !certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false))
                    continue;
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                if (!chain.Build(certificate)) continue;
                var intermediates = new X509Certificate2Collection();
                for (var i = 1; i < chain.ChainElements.Count - 1; i++)
                    intermediates.Add(new X509Certificate2(chain.ChainElements[i].Certificate));
                return (new X509Certificate2(certificate), intermediates);
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        throw new InvalidOperationException($"Le magasin personnel ne contient pas de certificat HTTPS reconnu pour {host}, valide jusqu’à la fin du partage.");
    }
}
