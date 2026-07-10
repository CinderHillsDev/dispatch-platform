using System.Text.Json;
using Dispatch.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Retry policy from the SQL config table (spec §12.3 spool.max_retries / spool.retry_delays_seconds),
/// cached briefly so the worker doesn't query per message. Falls back to the appsettings
/// <see cref="RetryOptions"/> defaults when a key is unset (spec §12.5 fallback).
/// </summary>
public sealed class SqlRetrySettings(IConfigRepository config, IOptions<RetryOptions> defaults) : IRetrySettings
{
    public const string MaxRetriesKey = "spool.max_retries";
    public const string RetryDelaysSecondsKey = "spool.retry_delays_seconds";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly RetryOptions _defaults = defaults.Value;
    private readonly Lock _lock = new();
    private RetryPolicy? _cache;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public async ValueTask<RetryPolicy> GetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cache;
        }

        var maxRetries = await ReadIntAsync(MaxRetriesKey, _defaults.MaxRetries, ct);
        var delays = await ReadDelaysAsync(ct);
        var snapshot = new RetryPolicy(maxRetries, delays);

        lock (_lock) { _cache = snapshot; _cachedAtUtc = DateTime.UtcNow; }
        return snapshot;
    }

    private async ValueTask<int> ReadIntAsync(string key, int fallback, CancellationToken ct)
    {
        var raw = await config.GetAsync(key, ct);
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    private async ValueTask<double[]> ReadDelaysAsync(CancellationToken ct)
    {
        var raw = await config.GetAsync(RetryDelaysSecondsKey, ct);
        if (raw is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<double[]>(raw);
                if (parsed is { Length: > 0 }) return parsed;
            }
            catch (JsonException) { /* malformed override - fall back to defaults */ }
        }
        return _defaults.EffectiveDelaysSeconds;
    }
}
