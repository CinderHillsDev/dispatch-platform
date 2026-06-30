using Dispatch.Core.Audit;
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
    ILogger<PurgeWorker> log,
    IAuditLog? audit = null) : BackgroundService
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

        // Audit log retention (general + shorter window for noisy security events).
        if (audit is not null)
        {
            var auditDeleted = await audit.PurgeAsync(o.AuditRetentionDays, o.AuditSecurityRetentionDays, ct);
            if (auditDeleted > 0) log.LogInformation("Purged {Count} audit_log rows", auditDeleted);
        }

        long dbSize;
        try { dbSize = await logs.GetDatabaseSizeBytesAsync(ct); } catch { dbSize = 0; }

        var result = new PurgeRunResult(DateTime.UtcNow, manual, failed + captured, logRows, dbSize);
        history.Record(result);
        if (audit is not null && (result.SpoolFilesDeleted + result.LogRowsDeleted) > 0)
            await audit.Lifecycle(manual ? "Storage cleanup ran (manual)" : "Storage cleanup ran",
                $"Removed {result.SpoolFilesDeleted} spool files and {result.LogRowsDeleted} log rows.");
        return result;
    }

    private async Task<int> PurgeLogAsync(string @event, int retentionDays, CancellationToken ct)
    {
        // 0 (or negative) means "keep forever" — the industry convention, consistent with the audit-log
        // purge and the "0 = keep forever" hint in the Settings UI. Without this, retention 0 would compute
        // a cutoff of "now" and delete everything.
        if (retentionDays <= 0) return 0;

        var deleted = await logs.PurgeByRetentionAsync(@event, retentionDays, ct);
        if (deleted > 0)
            log.LogInformation("Purged {Count} {Event} log rows older than {Days}d", deleted, @event, retentionDays);
        return deleted;
    }

    private async Task<int> RunSizePressureAsync(PurgeOptions o, CancellationToken ct)
    {
        // The 10 GB data-file cap only exists on SQL Server Express. On Standard/Enterprise/Azure or an
        // operator's own external server there is no cap, so the size-pressure purge never runs there.
        if (!await logs.IsSizeCappedEditionAsync(ct)) return 0;

        // Key off USED space, not the allocated file size (which never shrinks on DELETE) — so the loop
        // actually converges and frees a bounded amount.
        var used = await logs.GetDatabaseUsedBytesAsync(ct);
        var trigger = (long)(o.SizePressure.TriggerGb * BytesPerGb);
        if (used < trigger) return 0;

        var targetGb = Math.Min(o.SizePressure.TargetGb, o.SizePressure.TriggerGb);
        var target = (long)(targetGb * BytesPerGb);

        log.LogWarning("Database using {UsedGb:F1} GB (Express 10 GB cap) — archiving + purging oldest rows down to {TargetGb:F1} GB",
            used / (double)BytesPerGb, targetGb);

        // Archive rows to weekly JSONL before deleting them, so this emergency purge never silently loses
        // history. Message-log first (usually the bulk), then audit when relay_log is exhausted.
        var archive = new JsonlRowArchive(spool.ArchiveDir);
        int total = 0, archivedRelay = 0, archivedAudit = 0;
        while (!ct.IsCancellationRequested && await logs.GetDatabaseUsedBytesAsync(ct) > target)
        {
            var n = await logs.ArchiveAndDeleteOldestRelayLogAsync(500,
                (rows, _) => { archive.Append("relay_log", rows, "logged_at"); return Task.CompletedTask; }, ct);
            if (n > 0) { archivedRelay += n; total += n; await Task.Delay(100, ct); continue; }

            if (audit is not null)
            {
                n = await audit.ArchiveAndDeleteOldestAsync(500,
                    (rows, _) => { archive.Append("audit_log", rows, "logged_at"); return Task.CompletedTask; }, ct);
                if (n > 0) { archivedAudit += n; total += n; await Task.Delay(100, ct); continue; }
            }
            break;   // nothing left to free
        }

        if (total > 0)
        {
            log.LogWarning("Size-pressure purge archived + deleted {Relay} message-log and {Audit} audit rows to {Dir}",
                archivedRelay, archivedAudit, spool.ArchiveDir);
            if (audit is not null)
                await audit.Lifecycle("Size-pressure cleanup ran",
                    $"Database neared the 10 GB SQL Express cap; archived + deleted {archivedRelay} message-log and {archivedAudit} audit rows to {spool.ArchiveDir}.",
                    "Warning");
        }
        return total;
    }

    private static int PurgeFiles(string dir, int retentionDays, bool includeMeta)
    {
        // 0 (or negative) means "keep forever" — see PurgeLogAsync. Guard before computing the cutoff,
        // otherwise retention 0 would delete every file older than "now".
        if (retentionDays <= 0 || !Directory.Exists(dir)) return 0;
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
