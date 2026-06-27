using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Dispatch.Data;

/// <summary>
/// Transparent encryption for <c>config</c> rows flagged encrypted (spec §17.5, §19.5). On every platform it
/// uses AES-256-GCM with a random 256-bit key persisted as <c>.dispatch-key</c> in the directory set via
/// <see cref="UseKeyDirectory"/> (mode 600 on Unix; an inheritance-stripped ACL granting only SYSTEM,
/// Administrators, and the service account on Windows). Because the key lives in a portable file rather than a
/// machine-bound store, a database backup can be restored on a different host by also restoring the key file —
/// so disaster recovery and migration work the same on Windows, Linux, and macOS. An exfiltrated <c>config</c>
/// table still can't be decrypted without that host-local key file. If no key directory is set (or it's
/// unwritable) it falls back to a machine-derived key (weaker; surfaced via <see cref="UsedMachineKeyFallback"/>).
/// AES blob: base64( nonce[12] | tag[16] | ct ).
///
/// Backward compatibility: earlier Windows builds encrypted with DPAPI (LocalMachine). <see cref="Decrypt"/>
/// reads those legacy blobs on Windows when AES decryption fails, and they migrate to AES on the next save.
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

    /// <summary>True once at-rest encryption has fallen back to the (non-secret) machine-derived key because no
    /// writable key directory was available — a weaker posture the host should surface to the operator.</summary>
    public static bool UsedMachineKeyFallback { get; private set; }

    public static string Encrypt(string plaintext) => EncryptWith(Key.Value, plaintext);

    public static string Decrypt(string ciphertext)
    {
        // Current format (all platforms): AES-256-GCM with the portable key file.
        try
        {
            return DecryptWith(Key.Value, ciphertext);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows()
                                   && ex is CryptographicException or FormatException or ArgumentException)
        {
            // Not an AES blob we can open — likely a legacy DPAPI value from an older Windows build.
            // Re-encrypts to AES on the next save.
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), AppSalt, DataProtectionScope.LocalMachine));
        }
    }

    /// <summary>AES-256-GCM encrypt with an explicit key. Blob = base64( nonce[12] | tag[16] | ct ). The key
    /// is portable: anything <see cref="EncryptWith"/> produced can be decrypted on any host with the same key
    /// (the backup/restore-to-another-machine guarantee). Used internally and by tests.</summary>
    internal static string EncryptWith(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[NonceSize + TagSize + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ct, 0, blob, NonceSize + TagSize, ct.Length);
        return Convert.ToBase64String(blob);
    }

    /// <summary>AES-256-GCM decrypt with an explicit key. Throws <see cref="CryptographicException"/> if the
    /// key is wrong or the blob is tampered/too short (GCM authentication) — never returns wrong plaintext.</summary>
    internal static string DecryptWith(byte[] key, string ciphertext)
    {
        var blob = Convert.FromBase64String(ciphertext);
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short to be an AES-GCM blob.");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ct = blob.AsSpan(NonceSize + TagSize);
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(key, TagSize);
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
        UsedMachineKeyFallback = true;
        Console.Error.WriteLine(
            "SECURITY WARNING: at-rest encryption is using a machine-derived key because no writable key " +
            "directory was available. Stored secrets (provider credentials, TLS password) are weakly protected — " +
            "an attacker with the config DB and the host machine-id could decrypt them. Set DISPATCH_KEY_DIR to a " +
            "persistent, writable, private directory so a random key file is used instead.");
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
            // Create restricted, then write — keep the private key off other users. ProgramData is readable by
            // all users by default on Windows, so an explicit ACL matters there as much as mode 600 on Unix.
            using (File.Create(path)) { }
            if (OperatingSystem.IsWindows())
            {
                // Best-effort: an un-ACL'd key file still works (and Program Files is admin-write-only), so a
                // failure here must not abort key creation and trigger the weaker machine-key fallback.
                try { RestrictWindowsAcl(path); } catch { /* keep going with default ACLs */ }
            }
            else
            {
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

    /// <summary>Lock the key file down to SYSTEM, Administrators, and the running service account, with
    /// inheritance disabled — so other local users on a Windows box can't read it out of ProgramData.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictWindowsAcl(string path)
    {
        var fi = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);   // drop inherited ACEs
        void Grant(IdentityReference id) =>
            sec.AddAccessRule(new FileSystemAccessRule(id, FileSystemRights.FullControl, AccessControlType.Allow));
        Grant(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Grant(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (WindowsIdentity.GetCurrent().User is { } me) Grant(me);   // the service account, so it keeps access
        fi.SetAccessControl(sec);
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
