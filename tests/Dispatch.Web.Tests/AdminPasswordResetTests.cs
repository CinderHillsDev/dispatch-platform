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
}
