using System.Security.Cryptography;

namespace Dispatch.Core.Licensing;

/// <summary>
/// Verifies Dispatch license keys entirely OFFLINE (no network / no call-home), mirroring the FluxDeploy
/// license scheme: a compact 6-byte payload + a 64-byte ECDSA P-256 (SHA-256, IEEE-P1363) signature,
/// Crockford-Base32 encoded into a typeable <c>XXXXX-XXXXX-...</c> key. The signature is checked against the
/// license public key embedded in this assembly; the private key lives only in the issuer tool.
///
/// Payload (6 bytes): seqId[2 LE] | expiryMonth[1] | reserved[1] | reserved[1] | flags[1].
/// expiryMonth is months since 2024-01 (0 = perpetual); flags = version(2 MSB) | features(6 LSB).
/// </summary>
public sealed class LicenseVerifier(string publicKeyPem)
{
    private const int PayloadLength = 6;
    private const int SignatureLength = 64;          // ECDSA P-256, IEEE P1363 (r||s)
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford Base32
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Verifier using the license public key embedded in this assembly.</summary>
    public static LicenseVerifier Default() => new(EmbeddedPublicKey());

    /// <summary>Decode + authenticate a license key. Fails closed: any problem yields an invalid status.</summary>
    public LicenseStatus Verify(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return LicenseStatus.Invalid("no license key");

        var cleaned = new string(key.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        byte[] raw;
        try { raw = Base32Decode(cleaned); }
        catch { return LicenseStatus.Invalid("the license key contains invalid characters"); }
        if (raw.Length < PayloadLength + SignatureLength)
            return LicenseStatus.Invalid("the license key is malformed");

        var payload = raw[..PayloadLength];
        var signature = raw[PayloadLength..(PayloadLength + SignatureLength)];

        bool ok;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            ok = ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return LicenseStatus.Invalid("the license key could not be verified"); }
        if (!ok) return LicenseStatus.Invalid("the license key signature is invalid");

        var seq = (ushort)(payload[0] | (payload[1] << 8));
        var expiryMonth = payload[2];
        var version = (payload[5] >> 6) & 0x03;
        if (version != 0) return LicenseStatus.Invalid($"unsupported license version {version}");

        var perpetual = expiryMonth == 0;
        DateTime? expiresAt = perpetual ? null : Epoch.AddMonths(expiryMonth);
        var expired = !perpetual && expiresAt is { } e && e < DateTime.UtcNow;

        return new LicenseStatus(true, expired, $"DSP-{seq:D5}", perpetual, expiresAt,
            expired ? "the license has expired" : null);
    }

    /// <summary>The embedded license public key PEM (SPKI).</summary>
    public static string EmbeddedPublicKey()
    {
        var asm = typeof(LicenseVerifier).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("dispatch-license-public.pem", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded license public key not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ─── Crockford Base32 (no padding), matching the issuer tool ───

    internal static string Base32Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5) { bitsLeft -= 5; sb.Append(Alphabet[(buffer >> bitsLeft) & 0x1F]); }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    internal static byte[] Base32Decode(string encoded)
    {
        int buffer = 0, bitsLeft = 0;
        var result = new List<byte>();
        foreach (var c in encoded)
        {
            var val = Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"invalid Base32 character: {c}");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8) { bitsLeft -= 8; result.Add((byte)(buffer >> bitsLeft)); }
        }
        return result.ToArray();
    }

    /// <summary>Group a raw Base32 string into dash-separated blocks of 5 (display/entry format).</summary>
    internal static string FormatKey(string raw)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }
}
