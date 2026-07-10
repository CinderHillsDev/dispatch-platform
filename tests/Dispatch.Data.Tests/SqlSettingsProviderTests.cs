using Dispatch.Core.Configuration;
using Dispatch.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Dispatch.Data.Tests;

/// <summary>
/// Unit tests for the cached, SQL-config-backed settings providers (spec §12.3, §12.5). These exercise
/// the override/fallback logic against an in-memory config store, so they need no database.
/// </summary>
public class SqlSettingsProviderTests
{
    /// <summary>Minimal in-memory <see cref="IConfigRepository"/> - no encryption, no SQL.</summary>
    private sealed class MemoryConfig : IConfigRepository
    {
        private readonly Dictionary<string, string> _values = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigEntry>>(
                _values.Select(kv => new ConfigEntry(kv.Key, kv.Value, false, DateTime.UtcNow)).ToList());
    }

    [Fact]
    public async Task Retry_settings_fall_back_to_options_defaults_when_unset()
    {
        var config = new MemoryConfig();
        var defaults = new RetryOptions { MaxRetries = 3, DelaysSeconds = [30, 300, 1800] };
        var settings = new SqlRetrySettings(config, Options.Create(defaults));

        var policy = await settings.GetAsync();

        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal([30, 300, 1800], policy.DelaysSeconds);
    }

    [Fact]
    public async Task Retry_settings_read_overrides_from_config()
    {
        var config = new MemoryConfig();
        await config.SetAsync(SqlRetrySettings.MaxRetriesKey, "5");
        await config.SetAsync(SqlRetrySettings.RetryDelaysSecondsKey, "[10,60,120,600]");
        var settings = new SqlRetrySettings(config, Options.Create(new RetryOptions { MaxRetries = 3 }));

        var policy = await settings.GetAsync();

        Assert.Equal(5, policy.MaxRetries);
        Assert.Equal([10, 60, 120, 600], policy.DelaysSeconds);
        // DelayFor clamps to the last delay for attempts beyond the array.
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(600), policy.DelayFor(99));
    }

    [Fact]
    public async Task Purge_settings_fall_back_to_options_defaults_when_unset()
    {
        var config = new MemoryConfig();
        var defaults = new PurgeOptions();
        var settings = new SqlPurgeSettings(config, Options.Create(defaults));

        var o = await settings.GetAsync();

        Assert.Equal(defaults.Log.DeliveredRetentionDays, o.Log.DeliveredRetentionDays);
        Assert.Equal(defaults.Log.FailedRetentionDays, o.Log.FailedRetentionDays);
        Assert.Equal(defaults.SpoolFailedRetentionDays, o.SpoolFailedRetentionDays);
        Assert.Equal(defaults.CapturedRetentionDays, o.CapturedRetentionDays);
        Assert.Equal(defaults.SizePressure.TriggerGb, o.SizePressure.TriggerGb);
        Assert.Equal(defaults.SizePressure.TargetGb, o.SizePressure.TargetGb);
    }

    [Fact]
    public async Task Purge_settings_read_overrides_from_config()
    {
        var config = new MemoryConfig();
        await config.SetAsync(SqlPurgeSettings.LogDeliveredRetentionDaysKey, "14");
        await config.SetAsync(SqlPurgeSettings.LogFailedRetentionDaysKey, "120");
        await config.SetAsync(SqlPurgeSettings.SpoolFailedRetentionDaysKey, "45");
        await config.SetAsync(SqlPurgeSettings.CapturedRetentionDaysKey, "3");
        await config.SetAsync(SqlPurgeSettings.SizeTriggerGbKey, "8.5");
        await config.SetAsync(SqlPurgeSettings.SizeTargetGbKey, "8.0");
        var settings = new SqlPurgeSettings(config, Options.Create(new PurgeOptions()));

        var o = await settings.GetAsync();

        Assert.Equal(14, o.Log.DeliveredRetentionDays);
        Assert.Equal(120, o.Log.FailedRetentionDays);
        Assert.Equal(45, o.SpoolFailedRetentionDays);
        Assert.Equal(3, o.CapturedRetentionDays);
        Assert.Equal(8.5, o.SizePressure.TriggerGb);
        Assert.Equal(8.0, o.SizePressure.TargetGb);
    }
}
