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
        // Postgres has no DELETE ... LIMIT, so bound each batch by ctid membership.
        const string sql = """
            DELETE FROM relay_log
            WHERE ctid IN (
                SELECT ctid FROM relay_log
                WHERE event = @event AND logged_at < now() - @retentionDays * interval '1 day'
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
        const string sql = "SELECT pg_database_size(current_database());";
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<(long TableBytes, long RowCount)> GetRelayLogStatsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT pg_total_relation_size('relay_log')::bigint AS TableBytes,
                   (SELECT count(*) FROM relay_log)::bigint     AS RowCount;
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleAsync<(long, long)>(new CommandDefinition(sql, cancellationToken: ct));
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
            "DELETE FROM relay_log WHERE id = ANY(@ids);", new { ids }, cancellationToken: ct));
    }

    public async Task VacuumLogTablesAsync(CancellationToken ct = default)
    {
        // VACUUM cannot run inside a transaction block; each statement runs on its own. FULL rewrites the
        // table and returns space to the OS so pg_database_size drops and the size-pressure trigger clears.
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("VACUUM (FULL) relay_log;", cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("VACUUM (FULL) audit_log;", cancellationToken: ct));
    }

    // Materializes a Dapper row into a nullable-friendly read-only dictionary for archiving/serialization.
    internal static IReadOnlyDictionary<string, object?> ToRow(IDictionary<string, object> row) =>
        row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
