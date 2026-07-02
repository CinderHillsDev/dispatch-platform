using System.Security.Cryptography;
using Dispatch.Core.Licensing;

namespace Dispatch.Core.Tests;

public class LicenseVerifierTests
{
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Build a signed license key (matching the issuer format) for the given key pair + expiry month.
    private static string MakeKey(ECDsa signer, ushort seq = 42, byte expiryMonth = 0, int version = 0)
    {
        var payload = new byte[6];
        payload[0] = (byte)(seq & 0xFF);
        payload[1] = (byte)(seq >> 8);
        payload[2] = expiryMonth;
        payload[5] = (byte)((version & 0x03) << 6);
        var sig = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
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

        var s = v.Verify(MakeKey(ec, seq: 42, expiryMonth: 0));

        Assert.True(s.SignatureValid);
        Assert.False(s.Expired);
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

        var s = v.Verify(MakeKey(ec, expiryMonth: ExpiryMonthFor(DateTime.UtcNow.AddYears(1))));

        Assert.True(s.Licensed);
        Assert.False(s.Perpetual);
        Assert.NotNull(s.ExpiresAt);
    }

    [Fact]
    public void Flags_an_expired_key_as_not_licensed_but_authentic()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem());

        var s = v.Verify(MakeKey(ec, expiryMonth: ExpiryMonthFor(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc))));

        Assert.True(s.SignatureValid);   // authentic
        Assert.True(s.Expired);
        Assert.False(s.Licensed);        // but not usable
    }

    [Fact]
    public void Rejects_a_key_signed_by_a_different_key()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = new LicenseVerifier(other.ExportSubjectPublicKeyInfoPem());   // wrong public key

        var s = v.Verify(MakeKey(signer));

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

        var s = v.Verify(new string(chars));

        Assert.False(s.Licensed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-key")]
    public void Rejects_empty_or_garbage(string key)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = new LicenseVerifier(ec.ExportSubjectPublicKeyInfoPem()).Verify(key);
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
