using System.Security.Cryptography;

namespace Dispatch.Core.Licensing;

/// <summary>
/// Verifies Dispatch license keys entirely OFFLINE (no network / no call-home), mirroring the FluxDeploy
/// license scheme: a compact 6-byte payload + a 64-byte ECDSA P-256 (SHA-256, IEEE-P1363) signature,
/// Crockford-Base32 encoded into a typeable <c>XXXXX-XXXXX-...</c> key. The signature is checked against the
/// license public key embedded in this assembly; the private key lives only in the issuer tool.
///
/// The key is <b>node-locked</b>: the signature covers <c>payload || machineId</c>, where machineId is this
/// install's persisted GUID. The machineId is NOT stored in the key - the verifier appends the local machineId
/// before checking - so a key issued for one install fails to verify on any other (e.g. a reinstall, which
/// gets a fresh GUID). A leaked key can additionally be <b>revoked</b> by seqId via the embedded revocation
/// list, which ships (and grows) with each signed product release.
///
/// Payload (6 bytes): seqId[2 LE] | expiryMonth[1] | reserved[1] | reserved[1] | flags[1].
/// expiryMonth is months since 2024-01 (0 = perpetual); flags = version(2 MSB) | reserved(6 LSB).
/// </summary>
public sealed class LicenseVerifier
{
    private const int PayloadLength = 6;
    private const int SignatureLength = 64;          // ECDSA P-256, IEEE P1363 (r||s)
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford Base32
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _publicKeyPem;
    private readonly IReadOnlySet<ushort> _revokedSeqIds;

    public LicenseVerifier(string publicKeyPem) : this(publicKeyPem, LoadRevokedSeqIds()) { }

    /// <summary>Test seam: inject an explicit revoked-seqId set instead of the embedded list.</summary>
    internal LicenseVerifier(string publicKeyPem, IReadOnlySet<ushort> revokedSeqIds)
    {
        _publicKeyPem = publicKeyPem;
        _revokedSeqIds = revokedSeqIds;
    }

    /// <summary>Verifier using the license public key embedded in this assembly.</summary>
    public static LicenseVerifier Default() => new(EmbeddedPublicKey());

    /// <summary>
    /// Decode + authenticate a license key against this install's <paramref name="machineId"/>.
    /// Fails closed: any problem yields an invalid status.
    /// </summary>
    public LicenseStatus Verify(string? key, string? machineId)
    {
        if (string.IsNullOrWhiteSpace(key)) return LicenseStatus.Invalid("no license key");
        if (string.IsNullOrWhiteSpace(machineId)) return LicenseStatus.Invalid("no machine id");

        var cleaned = new string(key.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        byte[] raw;
        try { raw = Base32Decode(cleaned); }
        catch { return LicenseStatus.Invalid("the license key contains invalid characters"); }
        if (raw.Length < PayloadLength + SignatureLength)
            return LicenseStatus.Invalid("the license key is malformed");

        var payload = raw[..PayloadLength];
        var signature = raw[PayloadLength..(PayloadLength + SignatureLength)];
        var signed = SignedMessage(payload, machineId);

        bool ok;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(_publicKeyPem);
            ok = ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return LicenseStatus.Invalid("the license key could not be verified"); }
        if (!ok) return LicenseStatus.Invalid("the license key is not valid for this machine");

        var seq = (ushort)(payload[0] | (payload[1] << 8));
        var expiryMonth = payload[2];
        var version = (payload[5] >> 6) & 0x03;
        if (version != 0) return LicenseStatus.Invalid($"unsupported license version {version}");

        var licenseId = $"DSP-{seq:D5}";
        if (_revokedSeqIds.Contains(seq))
            return new LicenseStatus(true, false, true, licenseId, expiryMonth == 0, null, "the license has been revoked");

        var perpetual = expiryMonth == 0;
        DateTime? expiresAt = perpetual ? null : Epoch.AddMonths(expiryMonth);
        var expired = !perpetual && expiresAt is { } e && e < DateTime.UtcNow;

        return new LicenseStatus(true, expired, false, licenseId, perpetual, expiresAt,
            expired ? "the license has expired" : null);
    }

    /// <summary>The signed message: the 6-byte payload followed by the UTF-8 machine id (normalized).</summary>
    internal static byte[] SignedMessage(byte[] payload, string machineId)
    {
        var idBytes = System.Text.Encoding.UTF8.GetBytes(NormalizeMachineId(machineId));
        var signed = new byte[payload.Length + idBytes.Length];
        Buffer.BlockCopy(payload, 0, signed, 0, payload.Length);
        Buffer.BlockCopy(idBytes, 0, signed, payload.Length, idBytes.Length);
        return signed;
    }

    /// <summary>Machine ids are compared case-insensitively with surrounding whitespace trimmed.</summary>
    internal static string NormalizeMachineId(string machineId) => machineId.Trim().ToLowerInvariant();

    /// <summary>
    /// The revoked-seqId blacklist embedded in this assembly. Each product release ships the current list,
    /// so a key leaked into the wild is blocked once customers update. One seqId per line; <c>#</c> comments
    /// and blank lines ignored; entries may be a bare number or a <c>DSP-#####</c> id.
    /// </summary>
    private static IReadOnlySet<ushort> LoadRevokedSeqIds()
    {
        var asm = typeof(LicenseVerifier).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("dispatch-revoked-licenses.txt", StringComparison.Ordinal));
        var set = new HashSet<ushort>();
        if (name is null) return set;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            if (t.StartsWith("DSP-", StringComparison.OrdinalIgnoreCase)) t = t[4..];
            if (ushort.TryParse(t, out var seq)) set.Add(seq);
        }
        return set;
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
