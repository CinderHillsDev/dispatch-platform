using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dispatch.Core.Updates;

namespace Dispatch.Core.Tests;

public class UpdateBundleVerifierTests
{
    [Fact]
    public void Verifies_valid_signature_and_rejects_tampering_or_wrong_key()
    {
        using var key = RSA.Create(3072);
        var verifier = new UpdateBundleVerifier(key.ExportSubjectPublicKeyInfoPem());
        var manifest = Encoding.UTF8.GetBytes("""{"version":"1.2.3","sha256":"abc"}""");
        var sig = key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(verifier.VerifyManifestSignature(manifest, sig));

        var tampered = (byte[])manifest.Clone();
        tampered[10] ^= 0xFF;
        Assert.False(verifier.VerifyManifestSignature(tampered, sig));   // payload changed

        using var other = RSA.Create(3072);
        Assert.False(new UpdateBundleVerifier(other.ExportSubjectPublicKeyInfoPem()).VerifyManifestSignature(manifest, sig)); // wrong key
        Assert.False(verifier.VerifyManifestSignature(manifest, new byte[16]));   // garbage signature
    }

    [Fact]
    public void Embedded_public_key_verifies_an_openssl_signature()
    {
        // sample.manifest.json(.sig) were produced by `openssl dgst -sha256 -sign` with the private key
        // matching the embedded public key — proving the CI signer and the in-app verifier interoperate.
        var manifest = ReadResource("sample.manifest.json");
        var sig = ReadResource("sample.manifest.json.sig");
        Assert.True(UpdateBundleVerifier.Default().VerifyManifestSignature(manifest, sig));
    }

    [Fact]
    public void Payload_hash_matches_only_the_correct_digest()
    {
        var payload = Encoding.UTF8.GetBytes("the upgrade bundle bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        Assert.True(UpdateBundleVerifier.VerifyPayloadHash(new MemoryStream(payload), sha));
        Assert.True(UpdateBundleVerifier.VerifyPayloadHash(new MemoryStream(payload), sha.ToUpperInvariant()));  // case-insensitive
        Assert.False(UpdateBundleVerifier.VerifyPayloadHash(new MemoryStream(payload), new string('0', 64)));
    }

    [Fact]
    public void Release_pipeline_bundle_format_round_trips_through_openssl()
    {
        // Smoke of the actual release.yml mechanics (no production secret needed): build a payload +
        // manifest in the SAME shape release.yml emits, sign it with `openssl dgst -sha256 -sign` using an
        // ephemeral key, and confirm the real in-app verifier accepts it and rejects tampering. Catches
        // drift in the manifest format or signing algorithm between the workflow and the app.
        if (!OpensslAvailable()) return;   // skip on dev machines without openssl (CI has it)

        var dir = Directory.CreateTempSubdirectory("dispatch-bundle-smoke");
        try
        {
            var key = Path.Combine(dir.FullName, "k.key");
            var pub = Path.Combine(dir.FullName, "k.pub");
            Openssl(dir.FullName, "genrsa", "-out", key, "3072");
            Openssl(dir.FullName, "rsa", "-in", key, "-pubout", "-out", pub);

            var payload = Encoding.UTF8.GetBytes("pretend upgrade-bundle tarball bytes");
            var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
            // Keep this in sync with the `printf` manifest in .github/workflows/release.yml.
            var manifest = $"{{\"name\":\"dispatch\",\"version\":\"9.9.9\",\"arch\":\"linux-x64\",\"sha256\":\"{sha}\",\"minFromVersion\":\"0.0.0\",\"builtAt\":\"2026-01-01T00:00:00Z\",\"notesUrl\":\"https://example/v9.9.9\"}}\n";
            var manPath = Path.Combine(dir.FullName, "m.json");
            var sigPath = Path.Combine(dir.FullName, "m.json.sig");
            File.WriteAllText(manPath, manifest);
            Openssl(dir.FullName, "dgst", "-sha256", "-sign", key, "-out", sigPath, manPath);

            var verifier = new UpdateBundleVerifier(File.ReadAllText(pub));
            Assert.True(verifier.VerifyManifestSignature(File.ReadAllBytes(manPath), File.ReadAllBytes(sigPath)));
            Assert.True(UpdateBundleVerifier.VerifyPayloadHash(new MemoryStream(payload), sha));

            var tampered = File.ReadAllBytes(manPath);
            tampered[8] ^= 0xFF;
            Assert.False(verifier.VerifyManifestSignature(tampered, File.ReadAllBytes(sigPath)));
        }
        finally { dir.Delete(recursive: true); }
    }

    private static bool OpensslAvailable()
    {
        try { Openssl(Path.GetTempPath(), "version"); return true; }
        catch { return false; }
    }

    private static void Openssl(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("openssl")
        {
            WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"openssl {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    private static byte[] ReadResource(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"resource {suffix} not found");
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
