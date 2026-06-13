using Dispatch.Core.Configuration;
using Dispatch.Core.Logging;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Maintenance;

/// <summary>
/// Background retention + auto-purge (spec §6.10): deletes aged spool/failed and spool/captured files,
/// purges old relay_log rows per event type, and runs a size-pressure purge when the database nears the
/// SQL Server Express limit. Runs once on startup and then on a fixed interval. All work is best-effort —
/// a failure in one cycle is logged and never crashes the service.
/// </summary>
public sealed class PurgeWorker(
    SpoolDirectory spool,
    ILogMaintenance logs,
    IOptions<PurgeOptions> options,
    ILogger<PurgeWorker> log) : BackgroundService
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            log.LogInformation("Purge worker disabled");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(0.1, o.ScheduleIntervalHours));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(o, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Purge cycle failed");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunOnceAsync(PurgeOptions o, CancellationToken ct)
    {
        var failed = PurgeFiles(spool.FailedDir, o.SpoolFailedRetentionDays, includeMeta: true);
        var captured = PurgeFiles(spool.CapturedDir, o.CapturedRetentionDays, includeMeta: false);
        if (failed + captured > 0)
            log.LogInformation("Purged {Failed} failed and {Captured} captured spool files", failed, captured);

        await PurgeLogAsync("Delivered", o.Log.DeliveredRetentionDays, ct);
        await PurgeLogAsync("Failed", o.Log.FailedRetentionDays, ct);
        await PurgeLogAsync("Retrying", o.Log.RetryingRetentionDays, ct);
        await PurgeLogAsync("TestSent", o.Log.TestSentRetentionDays, ct);

        await RunSizePressureAsync(o, ct);
    }

    private async Task PurgeLogAsync(string @event, int retentionDays, CancellationToken ct)
    {
        var deleted = await logs.PurgeByRetentionAsync(@event, retentionDays, ct);
        if (deleted > 0)
            log.LogInformation("Purged {Count} {Event} log rows older than {Days}d", deleted, @event, retentionDays);
    }

    private async Task RunSizePressureAsync(PurgeOptions o, CancellationToken ct)
    {
        var size = await logs.GetDatabaseSizeBytesAsync(ct);
        var trigger = (long)(o.SizePressure.TriggerGb * BytesPerGb);
        var target = (long)(o.SizePressure.TargetGb * BytesPerGb);
        if (size < trigger) return;

        log.LogWarning("Database at {SizeGb:F1} GB — running size-pressure purge to {TargetGb:F1} GB",
            size / (double)BytesPerGb, o.SizePressure.TargetGb);

        while (!ct.IsCancellationRequested && await logs.GetDatabaseSizeBytesAsync(ct) >= target)
        {
            var deleted = await logs.PurgeOldestAsync(500, ct);
            if (deleted == 0) break;   // nothing left to delete; size is held by other objects
            await Task.Delay(100, ct);
        }
    }

    private static int PurgeFiles(string dir, int retentionDays, bool includeMeta)
    {
        if (!Directory.Exists(dir)) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var count = 0;
        foreach (var eml in Directory.EnumerateFiles(dir, "*.eml").ToList())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(eml) >= cutoff) continue;
                File.Delete(eml);
                count++;
                if (includeMeta)
                {
                    var meta = Path.ChangeExtension(eml, ".meta");
                    if (File.Exists(meta)) File.Delete(meta);
                }
            }
            catch { /* best-effort; a locked/in-use file is retried next cycle */ }
        }
        return count;
    }
}
