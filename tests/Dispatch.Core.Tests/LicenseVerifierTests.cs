using System.Security.Cryptography;
using Dispatch.Core.Licensing;

namespace Dispatch.Core.Tests;

public class LicenseVerifierTests
{
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Machine = "1f3c9a2e-0b7d-4c6a-9e11-5a2b3c4d5e6f";

    // Build a node-locked license key: signs payload || machineId (matching the issuer format).
    private static string MakeKey(ECDsa signer, string machineId = Machine,
        ushort seq = 42, byte expiryMonth = 0, int version = 0)
    {
        var payload = new byte[6];
        payload[0] = (byte)(seq & 0xFF);
        payload[1] = (byte)(seq >> 8);
        payload[2] = expiryMonth;
        payload[5] = (byte)((version & 0x03) << 6);
        var signed = LicenseVerifier.SignedMessage(payload, machineId);
        var sig = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var raw = new byte[payload.Length + sig.Length];
        Buffer.BlockCopy(payload, 0, raw, 0, payload.Length);
        Buffer.BlockCopy(sig, 0, raw, payload.Length, sig.Length);
        return LicenseVerifier.FormatKey(LicenseVerifier.Base32Encode(raw));
    }

    private static byte ExpiryMonthFor(DateTime when) =>
        (byte)Math.Clamp(((when.Year - Epoch.Year) * 12) + (when.Month - Epoch.Month), 1, 255);

    [Fact]
    public void Accepts_a_valid_perpetual_key()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var s = v.Verify(MakeKey(ec, seq: 42, expiryMonth: 0), Machine);

        Assert.True(s.SignatureValid);
        Assert.False(s.Expired);
        Assert.False(s.Revoked);
        Assert.True(s.Licensed);
        Assert.True(s.Perpetual);
        Assert.Null(s.ExpiresAt);
        Assert.Equal("DSP-00042", s.LicenseId);
    }

    [Fact]
    public void Accepts_a_valid_time_limited_key_in_date()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var s = v.Verify(MakeKey(ec, expiryMonth: ExpiryMonthFor(DateTime.UtcNow.AddYears(1))), Machine);

        Assert.True(s.Licensed);
        Assert.False(s.Perpetual);
        Assert.NotNull(s.ExpiresAt);
    }

    [Fact]
    public void Flags_an_expired_key_as_not_licensed_but_authentic()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var s = v.Verify(MakeKey(ec, expiryMonth: ExpiryMonthFor(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc))), Machine);

        Assert.True(s.SignatureValid);   // authentic
        Assert.True(s.Expired);
        Assert.False(s.Licensed);        // but not usable
    }

    [Fact]
    public void Rejects_a_key_bound_to_a_different_machine()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        // Key issued for one machine; presented on another (e.g. a reinstall with a fresh GUID).
        var key = MakeKey(ec, machineId: "aaaaaaaa-0000-0000-0000-000000000000");
        var s = v.Verify(key, "bbbbbbbb-1111-1111-1111-111111111111");

        Assert.False(s.SignatureValid);
        Assert.False(s.Licensed);
    }

    [Fact]
    public void Machine_id_match_is_case_insensitive()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var key = MakeKey(ec, machineId: Machine.ToLowerInvariant());
        var s = v.Verify(key, "  " + Machine.ToUpperInvariant() + "  ");

        Assert.True(s.Licensed);
    }

    [Fact]
    public void Rejects_when_no_machine_id_supplied()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var s = v.Verify(MakeKey(ec), "");

        Assert.False(s.Licensed);
        Assert.Equal("no machine id", s.Error);
    }

    [Fact]
    public void Flags_a_revoked_key_as_not_licensed_but_authentic()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem(), new HashSet<ushort> { 42 });

        var s = v.Verify(MakeKey(ec, seq: 42), Machine);

        Assert.True(s.SignatureValid);   // authentic signature...
        Assert.True(s.Revoked);          // ...but blacklisted
        Assert.False(s.Licensed);
        Assert.Equal("DSP-00042", s.LicenseId);

        // A different seqId signed by the same key is unaffected.
        var ok = v.Verify(MakeKey(ec, seq: 43), Machine);
        Assert.True(ok.Licensed);
        Assert.False(ok.Revoked);
    }

    [Fact]
    public void Rejects_a_key_signed_by_a_different_key()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(other.ExportSubjectPublicKeyInfoPem());   // wrong public key

        var s = v.Verify(MakeKey(signer), Machine);

        Assert.False(s.SignatureValid);
        Assert.False(s.Licensed);
    }

    [Fact]
    public void Rejects_a_tampered_key()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());
        var key = MakeKey(ec);
        // Flip a character in the middle (keeps length/charset valid but corrupts the bytes).
        var chars = key.ToCharArray();
        var idx = key.Length / 2;
        if (chars[idx] == '-') idx++;
        chars[idx] = chars[idx] == 'A' ? 'B' : 'A';

        var s = v.Verify(new string(chars), Machine);

        Assert.False(s.Licensed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-key")]
    public void Rejects_empty_or_garbage(string key)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem()).Verify(key, Machine);
        Assert.False(s.SignatureValid);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public void Embedded_public_key_loads_as_a_usable_ecdsa_key()
    {
        // Guards the embedded resource name + PEM parse; the private half lives only in the issuer tool.
        var pem = LicenseVerifier.EmbeddedPublicKey();
        Assert.Contains("BEGIN PUBLIC KEY", pem);
        using var ec = ECDsa.Create();
        ec.ImportFromPem(pem);   // throws if not a valid key
        Assert.Equal(256, ec.KeySize);
    }
}
