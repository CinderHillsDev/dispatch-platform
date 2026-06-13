using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Data;

/// <summary>
/// Transparent encryption for <c>config</c> rows flagged encrypted (spec §19.5). Uses AES-256-GCM with a
/// machine-derived key (PBKDF2 from /etc/machine-id where available, else the machine name). The key is
/// held in memory only. Blob layout: base64( nonce[12] | tag[16] | ciphertext ).
/// </summary>
public static class SecureConfig
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AppSalt = Encoding.UTF8.GetBytes("Dispatch.SMTP.Relay.v1.config-salt");
    private static readonly Lazy<byte[]> Key = new(DeriveKey);

    public static string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(Key.Value, TagSize);
        aes.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[NonceSize + TagSize + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ct, 0, blob, NonceSize + TagSize, ct.Length);
        return Convert.ToBase64String(blob);
    }

    public static string Decrypt(string ciphertext)
    {
        var blob = Convert.FromBase64String(ciphertext);
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ct = blob.AsSpan(NonceSize + TagSize);
        var pt = new byte[ct.Length];

        using var aes = new AesGcm(Key.Value, TagSize);
        aes.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }

    private static byte[] DeriveKey()
    {
        var machine = TryReadMachineId() ?? Environment.MachineName;
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machine), AppSalt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static string? TryReadMachineId()
    {
        try
        {
            return File.Exists("/etc/machine-id")
                ? File.ReadAllText("/etc/machine-id").Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
