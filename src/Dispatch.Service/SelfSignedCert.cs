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

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Dispatch SMTP Relay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));   // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        try { san.AddDnsName(Environment.MachineName); } catch { /* best-effort */ }
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
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
