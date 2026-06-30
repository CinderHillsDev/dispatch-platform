namespace Dispatch.Core.Logging;

/// <summary>relay_log maintenance for the purge worker (spec §6.10): time-based + size-pressure deletes.</summary>
public interface ILogMaintenance
{
    /// <summary>Deletes rows of <paramref name="event"/> older than <paramref name="retentionDays"/>, in
    /// batches with a short pause, to avoid lock contention. Returns total rows deleted.</summary>
    Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default);

    /// <summary>Approximate database size in bytes (for size-pressure purge).</summary>
    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default);

    /// <summary>True only when the database is a SQL Server Express edition, which caps each database's data
    /// files at 10 GB. The size-pressure purge applies only here; a Standard/Enterprise/Azure/external server
    /// has no such cap, so size-pressure is skipped entirely.</summary>
    Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default);

    /// <summary>Deletes the oldest N relay_log rows. Returns rows deleted.</summary>
    Task<int> PurgeOldestAsync(int batchSize, CancellationToken ct = default);
}
