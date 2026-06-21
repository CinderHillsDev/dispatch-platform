using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Data;

/// <summary>
/// Transparent encryption for <c>config</c> rows flagged encrypted (spec §17.5, §19.5). On Windows it uses
/// DPAPI (<see cref="ProtectedData"/>, LocalMachine scope) so no key material is stored. On Linux/macOS it
/// uses AES-256-GCM with a random 256-bit key persisted as <c>.dispatch-key</c> (mode 600) in the directory
/// set via <see cref="UseKeyDirectory"/> — so an exfiltrated <c>config</c> table can't be decrypted without
/// also obtaining that host-local key file. If no key directory is set (or it's unwritable) it falls back to
/// a machine-derived key. A given machine uses one scheme. AES blob: base64( nonce[12] | tag[16] | ct ).
/// </summary>
public static class SecureConfig
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string KeyFileName = ".dispatch-key";
    private static readonly byte[] AppSalt = Encoding.UTF8.GetBytes("Dispatch.SMTP.Relay.v1.config-salt");
    private static string? _keyDir;
    private static readonly Lazy<byte[]> Key = new(DeriveKey);

    /// <summary>Sets the durable directory holding the random AES key (non-Windows). Call before the first
    /// Encrypt/Decrypt and point it at a persistent location (the spool/data dir).</summary>
    public static void UseKeyDirectory(string dir) => _keyDir = dir;

    public static string Encrypt(string plaintext)
    {
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), AppSalt, DataProtectionScope.LocalMachine));

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
        if (OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), AppSalt, DataProtectionScope.LocalMachine));

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
        // Preferred: a random 256-bit key persisted in the configured durable directory (mode 600).
        if (_keyDir is { } dir && TryLoadOrCreateKeyFile(dir) is { } fileKey)
            return fileKey;

        // Fallback only when no writable key directory is available: a machine-derived key. Weaker (the
        // machine id is not secret) but keeps the app functional; UseKeyDirectory should be set in normal runs.
        var machine = TryReadMachineId() ?? Environment.MachineName;
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machine), AppSalt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static byte[]? TryLoadOrCreateKeyFile(string dir)
    {
        try
        {
            var path = Path.Combine(dir, KeyFileName);
            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length == 32) return existing;
            }
            Directory.CreateDirectory(dir);
            var key = RandomNumberGenerator.GetBytes(32);
            // Create restricted, then write — keep the private key off other users.
            if (!OperatingSystem.IsWindows())
            {
                using (File.Create(path)) { }
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 600
            }
            File.WriteAllText(path, Convert.ToBase64String(key));
            return key;
        }
        catch
        {
            return null;   // fall back to the machine-derived key
        }
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
