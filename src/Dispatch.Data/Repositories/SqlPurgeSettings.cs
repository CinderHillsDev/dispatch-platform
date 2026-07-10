using System.Globalization;
using Dispatch.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Retention + auto-purge thresholds from the SQL config table (spec §12.3 purge.*), cached briefly so
/// the size-pressure loop doesn't re-query SQL. Falls back to the appsettings <see cref="PurgeOptions"/>
/// defaults when a key is unset (spec §12.5 fallback).
/// </summary>
public sealed class SqlPurgeSettings(IConfigRepository config, IOptions<PurgeOptions> defaults) : IPurgeSettings
{
    public const string LogDeliveredRetentionDaysKey = "purge.log_delivered_retention_days";
    public const string LogFailedRetentionDaysKey = "purge.log_failed_retention_days";
    public const string SizeTriggerGbKey = "purge.size_trigger_gb";
    public const string SizeTargetGbKey = "purge.size_target_gb";
    public const string SpoolFailedRetentionDaysKey = "purge.spool_failed_retention_days";
    public const string CapturedRetentionDaysKey = "purge.captured_retention_days";
    public const string AuditRetentionDaysKey = "purge.audit_retention_days";
    public const string AuditSecurityRetentionDaysKey = "purge.audit_security_retention_days";
    public const string ArchiveRetentionDaysKey = "purge.archive_retention_days";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly PurgeOptions _defaults = defaults.Value;
    private readonly Lock _lock = new();
    private PurgeOptions? _cache;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public async ValueTask<PurgeOptions> GetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cache;
        }

        // Only the spec-listed keys are SQL-overridable; the rest keep their appsettings defaults.
        var snapshot = new PurgeOptions
        {
            Enabled = _defaults.Enabled,
            ScheduleIntervalHours = _defaults.ScheduleIntervalHours,
            SpoolFailedRetentionDays = await ReadIntAsync(SpoolFailedRetentionDaysKey, _defaults.SpoolFailedRetentionDays, ct),
            CapturedRetentionDays = await ReadIntAsync(CapturedRetentionDaysKey, _defaults.CapturedRetentionDays, ct),
            AuditRetentionDays = await ReadIntAsync(AuditRetentionDaysKey, _defaults.AuditRetentionDays, ct),
            AuditSecurityRetentionDays = await ReadIntAsync(AuditSecurityRetentionDaysKey, _defaults.AuditSecurityRetentionDays, ct),
            ArchiveRetentionDays = await ReadIntAsync(ArchiveRetentionDaysKey, _defaults.ArchiveRetentionDays, ct),
            Log = new PurgeOptions.LogRetention
            {
                DeliveredRetentionDays = await ReadIntAsync(LogDeliveredRetentionDaysKey, _defaults.Log.DeliveredRetentionDays, ct),
                FailedRetentionDays = await ReadIntAsync(LogFailedRetentionDaysKey, _defaults.Log.FailedRetentionDays, ct),
                RetryingRetentionDays = _defaults.Log.RetryingRetentionDays,
                TestSentRetentionDays = _defaults.Log.TestSentRetentionDays,
            },
            SizePressure = new PurgeOptions.SizePressureOptions
            {
                TriggerGb = await ReadDoubleAsync(SizeTriggerGbKey, _defaults.SizePressure.TriggerGb, ct),
                TargetGb = await ReadDoubleAsync(SizeTargetGbKey, _defaults.SizePressure.TargetGb, ct),
            },
        };

        lock (_lock) { _cache = snapshot; _cachedAtUtc = DateTime.UtcNow; }
        return snapshot;
    }

    private async ValueTask<int> ReadIntAsync(string key, int fallback, CancellationToken ct)
    {
        var raw = await config.GetAsync(key, ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private async ValueTask<double> ReadDoubleAsync(string key, double fallback, CancellationToken ct)
    {
        var raw = await config.GetAsync(key, ct);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
