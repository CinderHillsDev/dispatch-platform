using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core.Updates;

/// <summary>
/// Authenticates an upgrade bundle before it is applied (spec: web-UI updates). Two independent checks,
/// both fail-closed: (1) the detached signature over the manifest verifies against the release public key
/// baked into the app (RSA PKCS#1 v1.5 / SHA-256 - interoperable with <c>openssl dgst -sha256 -sign</c>
/// in CI and <c>openssl dgst -verify</c> in the appliance updater), and (2) the payload tarball's SHA-256
/// matches the digest in the (now-trusted) manifest.
/// </summary>
public sealed class UpdateBundleVerifier(string publicKeyPem)
{
    /// <summary>Verifier using the release public key embedded in this assembly.</summary>
    public static UpdateBundleVerifier Default() => new(EmbeddedPublicKey());

    /// <summary>True if <paramref name="signature"/> is a valid RSA/SHA-256 signature of
    /// <paramref name="manifestBytes"/> under the release public key.</summary>
    public bool VerifyManifestSignature(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        try { return rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
        catch (CryptographicException) { return false; }   // malformed signature → not authentic
    }

    /// <summary>True if the SHA-256 of <paramref name="payload"/> equals <paramref name="expectedSha256Hex"/>.</summary>
    public static bool VerifyPayloadHash(Stream payload, string expectedSha256Hex)
    {
        using var sha = SHA256.Create();
        var actual = Convert.ToHexStringLower(sha.ComputeHash(payload));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes((expectedSha256Hex ?? "").Trim().ToLowerInvariant()));
    }

    /// <summary>The PEM release public key embedded from Updates/dispatch-update-public.pem.</summary>
    public static string EmbeddedPublicKey()
    {
        var asm = typeof(UpdateBundleVerifier).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("dispatch-update-public.pem", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded update public key not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
