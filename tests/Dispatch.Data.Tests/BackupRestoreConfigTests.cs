using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Tests;

/// <summary>
/// Backup and restore of config from one install to another (docs: operations/backup-restore). A Dispatch
/// backup is two things: the database and the <c>.dispatch-key</c> file. This proves the load-bearing
/// invariant on the real database and the real crypto: an encrypted config value written by "install A"
/// restores intact on "install B" WHEN the key file is restored alongside the database, and is provably
/// unreadable when it is not - so a database backup without the key is useless, exactly as the docs warn.
///
/// Uses <see cref="SecureConfig"/>'s explicit-key path (the same AES-256-GCM that
/// <c>SqlConfigRepository.SetAsync(..., encrypted: true)</c> stores) so the test is deterministic regardless
/// of the process-wide key directory other tests may have set.
/// </summary>
public class BackupRestoreConfigTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Encrypted_config_restores_on_another_install_only_with_the_key_file()
    {
        if (!sql.Available) return;

        // --- Install A: a random per-install key, persisted as .dispatch-key in the real on-disk format
        // (base64 of 32 bytes), and an encrypted provider secret written into A's database.
        var installA = Path.Combine(Path.GetTempPath(), $"dispatch-A-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installA);
        try
        {
            var keyA = RandomNumberGenerator.GetBytes(32);
            var keyFileA = Path.Combine(installA, ".dispatch-key");
            await File.WriteAllTextAsync(keyFileA, Convert.ToBase64String(keyA));

            var secret = $"sg-live-{Guid.NewGuid():N}";                 // e.g. a SendGrid API key
            var configKey = $"relay.sendgrid.api_key.{Guid.NewGuid():N}";
            var ciphertext = SecureConfig.EncryptWith(keyA, secret);   // exactly what the config repo stores

            await using (var db = await sql.Contexts.CreateDbContextAsync())
            {
                db.Config.Add(new ConfigEntity
                {
                    Key = configKey, Value = ciphertext, Encrypted = true, UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            // --- Back up = the database row above + the key file. Restore = read both back the way a fresh
            // install would: the ciphertext from the restored database, the key from the restored key file.
            string restoredCipher;
            await using (var db = await sql.Contexts.CreateDbContextAsync())
                restoredCipher = await db.Config.AsNoTracking()
                    .Where(c => c.Key == configKey).Select(c => c.Value).SingleAsync();

            var restoredKey = Convert.FromBase64String((await File.ReadAllTextAsync(keyFileA)).Trim());

            // Install B, WITH the restored key: the secret comes back intact.
            Assert.Equal(secret, SecureConfig.DecryptWith(restoredKey, restoredCipher));

            // Install B, database restored but WITHOUT the key (B generated its own): unreadable, by design.
            // AES-GCM authentication throws rather than returning wrong plaintext - the secret stays safe and
            // is lost, which is precisely why the key file must be part of the backup.
            var otherInstallKey = RandomNumberGenerator.GetBytes(32);
            Assert.ThrowsAny<CryptographicException>(() => SecureConfig.DecryptWith(otherInstallKey, restoredCipher));
        }
        finally
        {
            Directory.Delete(installA, recursive: true);
        }
    }
}
