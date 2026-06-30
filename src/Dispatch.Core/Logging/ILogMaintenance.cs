using Dispatch.Core.Maintenance;

namespace Dispatch.Core.Logging;

/// <summary>relay_log maintenance for the purge worker (spec §6.10): time-based + size-pressure deletes.</summary>
public interface ILogMaintenance
{
    /// <summary>Deletes rows of <paramref name="event"/> older than <paramref name="retentionDays"/>, in
    /// batches with a short pause, to avoid lock contention. Returns total rows deleted.</summary>
    Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default);

    /// <summary>Approximate database file size in bytes (allocated; for the /health + storage display).</summary>
    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default);

    /// <summary>Approximate USED bytes inside the database files (drops as rows are deleted). The
    /// size-pressure purge keys off this so it frees a bounded amount and terminates, rather than the
    /// allocated file size (which SQL Server never shrinks on DELETE).</summary>
    Task<long> GetDatabaseUsedBytesAsync(CancellationToken ct = default);

    /// <summary>True only when the database is a SQL Server Express edition, which caps each database's data
    /// files at 10 GB. The size-pressure purge applies only here; a Standard/Enterprise/Azure/external server
    /// has no such cap, so size-pressure is skipped entirely.</summary>
    Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default);

    /// <summary>Size-pressure step: reads the oldest <paramref name="batch"/> relay_log rows, hands them to
    /// <paramref name="archive"/> (which must persist them) and only then deletes them. Returns rows deleted.
    /// If archiving throws, nothing is deleted.</summary>
    Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default);
}
