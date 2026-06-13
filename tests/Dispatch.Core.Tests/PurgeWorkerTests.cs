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
        public Task<int> PurgeOldestAsync(int batchSize, CancellationToken ct = default) => Task.FromResult(0);
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
            NullLogger<PurgeWorker>.Instance);

        await worker.RunOnceAsync(new PurgeOptions { SpoolFailedRetentionDays = 30, CapturedRetentionDays = 7 }, default);

        Assert.False(File.Exists(oldFailed));
        Assert.False(File.Exists(SpoolMeta.PathFor(oldFailed)));   // .meta sidecar removed too
        Assert.True(File.Exists(recentFailed));
        Assert.False(File.Exists(oldCaptured));
    }
}
