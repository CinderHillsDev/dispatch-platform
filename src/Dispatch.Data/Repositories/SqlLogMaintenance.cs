using Dapper;
using Dispatch.Core.Logging;
using Dispatch.Core.Maintenance;

namespace Dispatch.Data.Repositories;

/// <summary>relay_log purge operations (spec §6.10). Batched deletes with pauses to avoid lock contention.</summary>
public sealed class SqlLogMaintenance(SqlConnectionFactory factory) : ILogMaintenance
{
    private const int BatchSize = 1000;

    public async Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default)
    {
        // Neither engine supports DELETE ... LIMIT portably, so each batch is bounded by a subquery on the
        // primary key. (The Postgres original used ctid; id is the PK on both engines and reads the same.)
        // Batching matters more on SQLite than it did on Postgres: a single unbounded DELETE would hold the
        // one write lock for its whole duration and stall ingest behind it.
        var sql = $"""
            DELETE FROM relay_log
            WHERE id IN (
                SELECT id FROM relay_log
                WHERE event = @event AND {factory.Dialect.OlderThanDays("logged_at", "@retentionDays")}
                LIMIT @BatchSize);
            """;
        await using var cn = await factory.OpenAsync(ct);

        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var deleted = await cn.ExecuteAsync(new CommandDefinition(
                sql, new { BatchSize, @event, retentionDays }, cancellationToken: ct));
            total += deleted;
            if (deleted < BatchSize) break;
            await Task.Delay(100, ct);   // breathe between batches
        }
        return total;
    }

    public async Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        return await factory.Dialect.GetDatabaseSizeBytesAsync(cn, ct);
    }

    public async Task<(long TableBytes, long RowCount)> GetRelayLogStatsAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var tableBytes = await factory.Dialect.GetTableSizeBytesAsync(cn, "relay_log", ct);
        var rowCount = await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT CAST(count(*) AS bigint) FROM relay_log;", cancellationToken: ct));
        return (tableBytes, rowCount);
    }

    public async Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = (await cn.QueryAsync(new CommandDefinition(
            "SELECT * FROM relay_log ORDER BY logged_at ASC, id ASC LIMIT @batch;",
            new { batch }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return 0;

        // Archive first; if that throws, the rows are NOT deleted (the safety net must not lose data).
        await archive(rows.Select(r => ToRow((IDictionary<string, object>)r)).ToList(), ct);

        var ids = rows.Select(r => Convert.ToInt64(((IDictionary<string, object>)r)["id"])).ToArray();
        return await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM relay_log WHERE id IN @ids;", new { ids }, cancellationToken: ct));
    }

    public async Task VacuumLogTablesAsync(CancellationToken ct = default)
    {
        // Neither engine returns space to the OS on DELETE, so the size-pressure trigger in PurgeWorker only
        // clears after an explicit reclaim. How that is done differs enough to belong in the dialect:
        // Postgres rewrites each table with VACUUM (FULL); SQLite's VACUUM is whole-database and ignores the
        // table list. Either way this is a maintenance action holding heavy locks, never a hot-path call.
        await using var cn = await factory.OpenAsync(ct);
        await factory.Dialect.ReclaimSpaceAsync(cn, ["relay_log", "audit_log"], ct);
    }

    // Materializes a Dapper row into a nullable-friendly read-only dictionary for archiving/serialization.
    internal static IReadOnlyDictionary<string, object?> ToRow(IDictionary<string, object> row) =>
        row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
