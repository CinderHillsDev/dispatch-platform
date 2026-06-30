using Dispatch.Core.Configuration;
using Dispatch.Core.Logging;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Core.Tests;

public class PurgeWorkerTests
{
    private sealed class NoopLogMaintenance : ILogMaintenance
    {
        public Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetDatabaseUsedBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingLogMaintenance : ILogMaintenance
    {
        public List<string> RetentionPurgedEvents { get; } = [];
        public Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default)
        { RetentionPurgedEvents.Add(@event); return Task.FromResult(0); }
        public Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetDatabaseUsedBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default) => Task.FromResult(0);
    }

    // Records size-pressure activity and lets the test pick the SQL edition. Reports 50 GB used (over any
    // trigger) and deletes nothing, so the loop makes exactly one archive-and-delete attempt then stops.
    private sealed class SizePressureSpy(bool capped) : ILogMaintenance
    {
        public int ArchiveAndDeleteCalls { get; private set; }
        public Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default) => Task.FromResult(50L * 1024 * 1024 * 1024);
        public Task<long> GetDatabaseUsedBytesAsync(CancellationToken ct = default) => Task.FromResult(50L * 1024 * 1024 * 1024);
        public Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default) => Task.FromResult(capped);
        public Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default)
        { ArchiveAndDeleteCalls++; return Task.FromResult(0); }
    }

    [Fact]
    public async Task Deletes_aged_failed_and_captured_files_keeps_recent()
    {
        using var t = new TempSpool();

        var oldFailed = Path.Combine(t.Spool.FailedDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(oldFailed, "old");
        File.WriteAllText(SpoolMeta.PathFor(oldFailed), "{}");
        File.SetLastWriteTimeUtc(oldFailed, DateTime.UtcNow.AddDays(-60));

        var recentFailed = Path.Combine(t.Spool.FailedDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(recentFailed, "new");

        var oldCaptured = Path.Combine(t.Spool.CapturedDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(oldCaptured, "old");
        File.SetLastWriteTimeUtc(oldCaptured, DateTime.UtcNow.AddDays(-30));

        var disk = new DiskMonitor(t.Spool, new IntakeState(), _ => long.MaxValue, NullLogger<DiskMonitor>.Instance);
        var worker = new PurgeWorker(t.Spool, new NoopLogMaintenance(), disk,
            new OptionsPurgeSettings(new PurgeOptions { SpoolFailedRetentionDays = 30, CapturedRetentionDays = 7 }),
            new PurgeHistory(),
            NullLogger<PurgeWorker>.Instance);

        await worker.RunOnceAsync(new PurgeOptions { SpoolFailedRetentionDays = 30, CapturedRetentionDays = 7 }, default);

        Assert.False(File.Exists(oldFailed));
        Assert.False(File.Exists(SpoolMeta.PathFor(oldFailed)));   // .meta sidecar removed too
        Assert.True(File.Exists(recentFailed));
        Assert.False(File.Exists(oldCaptured));
    }

    [Fact]
    public async Task Retention_zero_keeps_files_forever()
    {
        using var t = new TempSpool();

        // A year-old file in each managed dir — would be purged at any positive retention.
        var agedFailed = Path.Combine(t.Spool.FailedDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(agedFailed, "old");
        File.SetLastWriteTimeUtc(agedFailed, DateTime.UtcNow.AddDays(-365));

        var agedCaptured = Path.Combine(t.Spool.CapturedDir, $"{Guid.NewGuid()}.eml");
        File.WriteAllText(agedCaptured, "old");
        File.SetLastWriteTimeUtc(agedCaptured, DateTime.UtcNow.AddDays(-365));

        var disk = new DiskMonitor(t.Spool, new IntakeState(), _ => long.MaxValue, NullLogger<DiskMonitor>.Instance);
        var opts = new PurgeOptions
        {
            SpoolFailedRetentionDays = 0,
            CapturedRetentionDays = 0,
            SizePressure = new PurgeOptions.SizePressureOptions { TriggerGb = 9.5, TargetGb = 9.0 },
        };
        var worker = new PurgeWorker(t.Spool, new NoopLogMaintenance(), disk,
            new OptionsPurgeSettings(opts), new PurgeHistory(), NullLogger<PurgeWorker>.Instance);

        await worker.RunOnceAsync(opts, default);

        // 0 = keep forever (industry convention, consistent with the audit-log purge).
        Assert.True(File.Exists(agedFailed));
        Assert.True(File.Exists(agedCaptured));
    }

    [Fact]
    public async Task Retention_zero_skips_log_row_purge()
    {
        using var t = new TempSpool();
        var disk = new DiskMonitor(t.Spool, new IntakeState(), _ => long.MaxValue, NullLogger<DiskMonitor>.Instance);
        var rec = new RecordingLogMaintenance();
        var opts = new PurgeOptions
        {
            Log = new PurgeOptions.LogRetention
            {
                DeliveredRetentionDays = 0,
                FailedRetentionDays = 0,
                RetryingRetentionDays = 0,
                TestSentRetentionDays = 0,
            },
            SizePressure = new PurgeOptions.SizePressureOptions { TriggerGb = 9.5, TargetGb = 9.0 },
        };
        var worker = new PurgeWorker(t.Spool, rec, disk,
            new OptionsPurgeSettings(opts), new PurgeHistory(), NullLogger<PurgeWorker>.Instance);

        await worker.RunOnceAsync(opts, default);

        // 0 = keep forever: no DELETE issued for any log event.
        Assert.Empty(rec.RetentionPurgedEvents);
    }

    [Fact]
    public async Task Archive_files_pruned_by_retention()
    {
        using var t = new TempSpool();
        var aged = Path.Combine(t.Spool.ArchiveDir, "relay_log-2020-W01.jsonl");
        File.WriteAllText(aged, "{}");
        File.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-30));
        var disk = new DiskMonitor(t.Spool, new IntakeState(), _ => long.MaxValue, NullLogger<DiskMonitor>.Instance);

        // 0 = keep forever.
        var keep = new PurgeOptions { ArchiveRetentionDays = 0 };
        await new PurgeWorker(t.Spool, new NoopLogMaintenance(), disk, new OptionsPurgeSettings(keep), new PurgeHistory(), NullLogger<PurgeWorker>.Instance)
            .RunOnceAsync(keep, default);
        Assert.True(File.Exists(aged));

        // >0 = prune aged archives.
        var prune = new PurgeOptions { ArchiveRetentionDays = 7 };
        await new PurgeWorker(t.Spool, new NoopLogMaintenance(), disk, new OptionsPurgeSettings(prune), new PurgeHistory(), NullLogger<PurgeWorker>.Instance)
            .RunOnceAsync(prune, default);
        Assert.False(File.Exists(aged));
    }

    [Fact]
    public async Task Size_pressure_runs_only_on_express()
    {
        using var t = new TempSpool();
        var disk = new DiskMonitor(t.Spool, new IntakeState(), _ => long.MaxValue, NullLogger<DiskMonitor>.Instance);
        var opts = new PurgeOptions { SizePressure = new PurgeOptions.SizePressureOptions { TriggerGb = 9.5, TargetGb = 9.0 } };

        // Express edition + 50 GB > 9.5 GB trigger → size-pressure archives + purges the oldest rows.
        var express = new SizePressureSpy(capped: true);
        await new PurgeWorker(t.Spool, express, disk, new OptionsPurgeSettings(opts), new PurgeHistory(), NullLogger<PurgeWorker>.Instance)
            .RunOnceAsync(opts, default);
        Assert.True(express.ArchiveAndDeleteCalls >= 1);

        // Non-Express (no 10 GB cap) → size-pressure is skipped even at 50 GB.
        var standard = new SizePressureSpy(capped: false);
        await new PurgeWorker(t.Spool, standard, disk, new OptionsPurgeSettings(opts), new PurgeHistory(), NullLogger<PurgeWorker>.Instance)
            .RunOnceAsync(opts, default);
        Assert.Equal(0, standard.ArchiveAndDeleteCalls);
    }
}
