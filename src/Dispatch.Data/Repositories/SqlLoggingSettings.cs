using Dispatch.Core.Configuration;
using Dispatch.Core.Logging;

namespace Dispatch.Data.Repositories;

/// <summary>relay_log suppression toggles from the SQL config table, cached briefly (spec §12.3).</summary>
public sealed class SqlLoggingSettings(IConfigRepository config) : ILoggingSettings
{
    public const string LogDeliveredKey = "logging.log_delivered";
    public const string LogRetryingKey = "logging.log_retrying";
    public const string LogDeniedKey = "logging.log_denied";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly Lock _lock = new();
    private (bool Delivered, bool Retrying, bool Denied) _cache = (true, true, true);
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public async ValueTask<bool> LogDeliveredAsync(CancellationToken ct = default) => (await GetAsync(ct)).Delivered;
    public async ValueTask<bool> LogRetryingAsync(CancellationToken ct = default) => (await GetAsync(ct)).Retrying;
    public async ValueTask<bool> LogDeniedAsync(CancellationToken ct = default) => (await GetAsync(ct)).Denied;

    private async ValueTask<(bool Delivered, bool Retrying, bool Denied)> GetAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cache;
        }

        var snapshot = (
            await ReadBoolAsync(LogDeliveredKey, ct),
            await ReadBoolAsync(LogRetryingKey, ct),
            await ReadBoolAsync(LogDeniedKey, ct));

        lock (_lock) { _cache = snapshot; _cachedAtUtc = DateTime.UtcNow; }
        return snapshot;
    }

    private async ValueTask<bool> ReadBoolAsync(string key, CancellationToken ct)
    {
        var value = await config.GetAsync(key, ct);
        return value is null || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);   // default on
    }
}
