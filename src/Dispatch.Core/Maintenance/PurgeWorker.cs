using Dispatch.Core.Configuration;
using Dispatch.Core.Logging;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    DiskMonitor diskMonitor,
    IPurgeSettings settings,
    PurgeHistory history,
    ILogger<PurgeWorker> log) : BackgroundService
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Resolve the (live, SQL-backed) settings once per cycle — retention/threshold edits in the web
        // UI take effect on the next cycle without a restart.
        var initial = await settings.GetAsync(ct);
        if (!initial.Enabled)
        {
            log.LogInformation("Purge worker disabled");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var o = await settings.GetAsync(ct);
            try
            {
                await RunOnceAsync(o, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Purge cycle failed");
            }

            var interval = TimeSpan.FromHours(Math.Max(0.1, o.ScheduleIntervalHours));
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<PurgeRunResult> RunOnceAsync(PurgeOptions o, CancellationToken ct, bool manual = false)
    {
        // Disk back-pressure backstop (spec §14.1): the fast DiskMonitor timer is primary, but the purge
        // cycle re-evaluates so intake state stays current even if the timer is delayed.
        diskMonitor.Evaluate();

        var failed = PurgeFiles(spool.FailedDir, o.SpoolFailedRetentionDays, includeMeta: true);
        var captured = PurgeFiles(spool.CapturedDir, o.CapturedRetentionDays, includeMeta: false);
        if (failed + captured > 0)
            log.LogInformation("Purged {Failed} failed and {Captured} captured spool files", failed, captured);

        var logRows = 0;
        logRows += await PurgeLogAsync("Delivered", o.Log.DeliveredRetentionDays, ct);
        logRows += await PurgeLogAsync("Failed", o.Log.FailedRetentionDays, ct);
        logRows += await PurgeLogAsync("Retrying", o.Log.RetryingRetentionDays, ct);
        logRows += await PurgeLogAsync("TestSent", o.Log.TestSentRetentionDays, ct);

        logRows += await RunSizePressureAsync(o, ct);

        long dbSize;
        try { dbSize = await logs.GetDatabaseSizeBytesAsync(ct); } catch { dbSize = 0; }

        var result = new PurgeRunResult(DateTime.UtcNow, manual, failed + captured, logRows, dbSize);
        history.Record(result);
        return result;
    }

    private async Task<int> PurgeLogAsync(string @event, int retentionDays, CancellationToken ct)
    {
        var deleted = await logs.PurgeByRetentionAsync(@event, retentionDays, ct);
        if (deleted > 0)
            log.LogInformation("Purged {Count} {Event} log rows older than {Days}d", deleted, @event, retentionDays);
        return deleted;
    }

    private async Task<int> RunSizePressureAsync(PurgeOptions o, CancellationToken ct)
    {
        var size = await logs.GetDatabaseSizeBytesAsync(ct);
        var trigger = (long)(o.SizePressure.TriggerGb * BytesPerGb);
        var target = (long)(o.SizePressure.TargetGb * BytesPerGb);
        if (size < trigger) return 0;

        log.LogWarning("Database at {SizeGb:F1} GB — running size-pressure purge to {TargetGb:F1} GB",
            size / (double)BytesPerGb, o.SizePressure.TargetGb);

        var total = 0;
        while (!ct.IsCancellationRequested && await logs.GetDatabaseSizeBytesAsync(ct) >= target)
        {
            var deleted = await logs.PurgeOldestAsync(500, ct);
            if (deleted == 0) break;   // nothing left to delete; size is held by other objects
            total += deleted;
            await Task.Delay(100, ct);
        }
        return total;
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
