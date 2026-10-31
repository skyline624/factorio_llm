using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Factorio.Agent.Host;

/// <summary>HTTP client for the Gestion server: certificates are verified against the private CA only, never disabled.</summary>
public static class GestionHttp
{
    public static HttpClient Create(string caPemPath)
    {
        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPemFile(caPemPath);
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Do not preserve the inner exception: it may quote file contents.
            throw new InvalidDataException("The Gestion CA certificate file is missing or is not a valid PEM certificate.");
        }
        if (roots.Count == 0)
            throw new InvalidDataException("The Gestion CA certificate file contains no certificate.");
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => Validate(roots, certificate, chain, errors)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static bool Validate(X509Certificate2Collection roots, X509Certificate2? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        // Only an untrusted chain is re-evaluated; a name mismatch or a missing certificate stays fatal.
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        // The private CA publishes no CRL, so revocation cannot be checked (Node behaves the same with the CA as `ca`).
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
            foreach (var element in presented.ChainElements.Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(certificate);
    }
}
