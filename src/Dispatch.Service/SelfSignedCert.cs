using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Dispatch.Service;

/// <summary>
/// Supplies the dashboard's TLS certificate when the operator hasn't configured one (spec §17.2 — the admin
/// UI is HTTPS-only). A self-signed cert is generated once and persisted to the content root so it stays
/// stable across restarts (no new browser-trust prompt each time); it's regenerated only when missing,
/// unreadable, or within a day of expiry.
/// </summary>
public static class SelfSignedCert
{
    public static X509Certificate2 GetOrCreate(string dir)
    {
        var path = Path.Combine(dir, "dispatch-webui.pfx");

        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(1)) return existing;
            }
            catch { /* unreadable / expired — fall through and regenerate */ }
        }

        // ECDSA P-256 (modern, smaller, faster) instead of RSA-2048; serverAuth only.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Dispatch SMTP Relay", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: false));   // ECDHE server cert signs the handshake
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));   // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        try { san.AddDnsName(Environment.MachineName); } catch { /* best-effort */ }
        req.CertificateExtensions.Add(san.Build());

        // 2-year validity (auto-regenerated within a day of expiry on startup) rather than 5 years.
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = cert.Export(X509ContentType.Pfx);
        try
        {
            File.WriteAllBytes(path, pfx);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 600 — contains the private key
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist the self-signed dashboard certificate to {Path} (a new one will be generated next start)", path);
        }

        Log.Information("Dashboard TLS: using a self-signed certificate. Configure WebUi:TlsCertPath for a trusted certificate (spec §17.2).");
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }
}
