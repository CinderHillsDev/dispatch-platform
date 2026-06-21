using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Builds the SMTP listener's STARTTLS certificate as a password-protected PFX written to the data dir, so
/// operators never deal with a file path: they either generate a self-signed cert or upload a cert + key.
/// The listener loads it from <c>listener.tls_cert_path</c> at startup (so changes apply on restart).
/// </summary>
public static class ListenerCert
{
    public const string FileName = "dispatch-smtp.pfx";

    /// <summary>Generate a self-signed cert and write it as a PFX. Returns (absolute path, random password).</summary>
    public static (string Path, string Password) Generate(string dir, string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        try { san.AddDnsName(Environment.MachineName); } catch { /* best-effort */ }
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pw = NewPassword();
        return Write(dir, cert.Export(X509ContentType.Pfx, pw), pw);
    }

    /// <summary>Build a PFX from an uploaded PEM cert + PEM private key. Throws on invalid input.</summary>
    public static (string Path, string Password) FromPem(string dir, string certPem, string keyPem)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
        var pw = NewPassword();
        return Write(dir, cert.Export(X509ContentType.Pfx, pw), pw);
    }

    private static (string, string) Write(string dir, byte[] pfx, string password)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        if (!OperatingSystem.IsWindows())
        {
            using (File.Create(path)) { }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 600 — holds the private key
        }
        File.WriteAllBytes(path, pfx);
        return (path, password);
    }

    public static void Delete(string dir)
    {
        try { File.Delete(Path.Combine(dir, FileName)); } catch { /* best-effort */ }
    }

    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
}
