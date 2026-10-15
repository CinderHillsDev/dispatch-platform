using Dispatch.Core.Configuration;
using Dispatch.Web.Auth;

namespace Dispatch.Web.Tests;

/// <summary>
/// AuthEndpoints.SetPasswordAsync is the logic shared by POST /auth/password and the
/// `reset-admin-password` CLI (Dispatch.Service/Program.cs) - both must hash, store, and (when replacing
/// an existing password) invalidate other sessions identically. Exercised directly against a fake
/// IConfigRepository so it needs no HTTP host, matching how migrate-database's logic is tested via
/// DatabaseMigrator rather than through Program.cs's CLI entry point.
/// </summary>
public class AdminPasswordResetTests
{
    [Fact]
    public async Task Rejects_a_weak_password_and_writes_nothing()
    {
        var config = new FakeConfigRepository();

        var error = await AuthEndpoints.SetPasswordAsync(config, "weak");

        Assert.NotNull(error);
        Assert.Null(await config.GetAsync(AuthEndpoints.PasswordHashKey));
        Assert.Null(await config.GetAsync(ConfigKeys.WebUiSessionEpoch));
    }

    [Fact]
    public async Task First_run_sets_the_hash_without_bumping_the_session_epoch()
    {
        // No prior password: there are no other sessions to invalidate.
        var config = new FakeConfigRepository();

        var error = await AuthEndpoints.SetPasswordAsync(config, "Str0ngPassphrase");

        Assert.Null(error);
        var hash = await config.GetAsync(AuthEndpoints.PasswordHashKey);
        Assert.NotNull(hash);
        Assert.True(BCrypt.Net.BCrypt.Verify("Str0ngPassphrase", hash));
        Assert.Null(await config.GetAsync(ConfigKeys.WebUiSessionEpoch));
    }

    [Fact]
    public async Task Resetting_an_existing_password_replaces_the_hash_and_bumps_the_session_epoch()
    {
        // This is the "forgot the admin password" / reset-admin-password CLI path: a password already
        // exists, so every OTHER existing dashboard session must be invalidated by the reset.
        var config = new FakeConfigRepository();
        await config.SetAsync(AuthEndpoints.PasswordHashKey, BCrypt.Net.BCrypt.HashPassword("OldPassphrase99", 12));
        await config.SetAsync(ConfigKeys.WebUiSessionEpoch, "3");

        var error = await AuthEndpoints.SetPasswordAsync(config, "NewPassphrase99");

        Assert.Null(error);
        var hash = await config.GetAsync(AuthEndpoints.PasswordHashKey);
        Assert.True(BCrypt.Net.BCrypt.Verify("NewPassphrase99", hash));
        Assert.False(BCrypt.Net.BCrypt.Verify("OldPassphrase99", hash));
        Assert.Equal("4", await config.GetAsync(ConfigKeys.WebUiSessionEpoch));
    }

    [Fact]
    public async Task Resetting_an_existing_password_with_no_prior_epoch_starts_it_at_one()
    {
        var config = new FakeConfigRepository();
        await config.SetAsync(AuthEndpoints.PasswordHashKey, BCrypt.Net.BCrypt.HashPassword("OldPassphrase99", 12));

        var error = await AuthEndpoints.SetPasswordAsync(config, "NewPassphrase99");

        Assert.Null(error);
        Assert.Equal("1", await config.GetAsync(ConfigKeys.WebUiSessionEpoch));
    }

    [Fact]
    public async Task The_password_hash_is_stored_encrypted_at_rest()
    {
        // The bcrypt hash is the credential: with it an attacker who can read the config table (a backup,
        // a dump, a path traversal) gets an offline brute-force target. It must land in an encrypted row,
        // like every other secret in the table (see SecureConfig).
        var config = new FakeConfigRepository();

        Assert.Null(await AuthEndpoints.SetPasswordAsync(config, "Str0ngPassphrase"));

        var row = Assert.Single(await config.GetAllAsync(), e => e.Key == AuthEndpoints.PasswordHashKey);
        Assert.True(row.Encrypted);
    }

    [Fact]
    public async Task A_hash_written_by_an_older_build_is_rewritten_encrypted_and_still_verifies()
    {
        // 0.7.2 and earlier wrote this row with encrypted: false, so an upgraded host keeps a plaintext
        // credential until the startup rewrite runs. The rewrite must preserve the hash (login still works)
        // and must be idempotent.
        var config = new FakeConfigRepository();
        var hash = BCrypt.Net.BCrypt.HashPassword("LegacyPassphrase99", 12);
        await config.SetAsync(AuthEndpoints.PasswordHashKey, hash, encrypted: false);

        Assert.True(await AuthEndpoints.EncryptStoredPasswordHashAsync(config));

        var row = Assert.Single(await config.GetAllAsync(), e => e.Key == AuthEndpoints.PasswordHashKey);
        Assert.True(row.Encrypted);
        Assert.True(BCrypt.Net.BCrypt.Verify("LegacyPassphrase99", row.Value));

        Assert.False(await AuthEndpoints.EncryptStoredPasswordHashAsync(config));
    }

    [Fact]
    public async Task The_hash_rewrite_does_nothing_before_a_password_exists()
    {
        var config = new FakeConfigRepository();

        Assert.False(await AuthEndpoints.EncryptStoredPasswordHashAsync(config));
        Assert.Null(await config.GetAsync(AuthEndpoints.PasswordHashKey));
    }
}
