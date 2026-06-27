using Dispatch.Data;

namespace Dispatch.Data.Tests;

// SecureConfig uses a machine-derived key; these run without a database.
public class SecureConfigTests
{
    [Fact]
    public void Encrypt_then_decrypt_round_trips()
    {
        const string secret = "mailgun-api-key-12345";
        var cipher = SecureConfig.Encrypt(secret);

        Assert.NotEqual(secret, cipher);
        Assert.Equal(secret, SecureConfig.Decrypt(cipher));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently()
    {
        // Random nonce per encryption → different ciphertext, both decrypt back.
        Assert.NotEqual(SecureConfig.Encrypt("x"), SecureConfig.Encrypt("x"));
    }

    [Fact]
    public void Tampered_ciphertext_fails_authentication()
    {
        var cipher = SecureConfig.Encrypt("secret");
        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF;                       // flip a ciphertext byte
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => SecureConfig.Decrypt(tampered));
    }

    [Fact]
    public void Key_is_portable_across_hosts()
    {
        // The disaster-recovery guarantee: a value encrypted with the .dispatch-key on one host decrypts on a
        // different host when the key file is restored. Simulate by decrypting with a separate byte[] holding
        // the same key bytes (a "restored" copy) — proving decryption is key-based, not machine-bound.
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var cipher = SecureConfig.EncryptWith(key, "provider-secret");

        var restoredKey = (byte[])key.Clone();
        Assert.Equal("provider-secret", SecureConfig.DecryptWith(restoredKey, cipher));
    }

    [Fact]
    public void Wrong_key_fails_to_decrypt()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var cipher = SecureConfig.EncryptWith(key, "provider-secret");

        var wrongKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        // GCM authentication must reject a wrong key — never return wrong plaintext.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => SecureConfig.DecryptWith(wrongKey, cipher));
    }
}
