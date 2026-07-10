using Dispatch.Core.Maintenance;

namespace Dispatch.Core.Logging;

/// <summary>relay_log maintenance for the purge worker (spec §6.10): time-based + size-pressure deletes.</summary>
public interface ILogMaintenance
{
    /// <summary>Deletes rows of <paramref name="event"/> older than <paramref name="retentionDays"/>, in
    /// batches with a short pause, to avoid lock contention. Returns total rows deleted.</summary>
    Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default);

    /// <summary>Physical database size in bytes (<c>pg_database_size</c>; for the /health + storage display
    /// and the optional size-pressure trigger).</summary>
    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default);

    /// <summary>Physical bytes and live row count of <c>relay_log</c> (<c>pg_total_relation_size</c> +
    /// <c>count(*)</c>), used to estimate how many oldest rows to free to hit a target size.</summary>
    Task<(long TableBytes, long RowCount)> GetRelayLogStatsAsync(CancellationToken ct = default);

    /// <summary>Size-pressure step: reads the oldest <paramref name="batch"/> relay_log rows, hands them to
    /// <paramref name="archive"/> (which must persist them) and only then deletes them. Returns rows deleted.
    /// If archiving throws, nothing is deleted.</summary>
    Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default);

    /// <summary>Reclaims physical space after a size-pressure purge by running <c>VACUUM FULL</c> on the log
    /// tables, so <c>pg_database_size</c> actually shrinks and the size-pressure trigger clears. Postgres does
    /// not return space to the OS on plain DELETE/VACUUM; this is invoked only from the opt-in size-pressure
    /// path (it briefly takes an exclusive lock on the log tables).</summary>
    Task VacuumLogTablesAsync(CancellationToken ct = default);
}
