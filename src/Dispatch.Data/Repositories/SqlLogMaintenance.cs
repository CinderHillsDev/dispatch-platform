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
        const string sql = """
            DELETE TOP (@BatchSize) FROM relay_log
            WHERE event = @event AND logged_at < DATEADD(DAY, -@retentionDays, SYSUTCDATETIME());
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
        const string sql = "SELECT CAST(SUM(size) AS BIGINT) * 8 * 1024 FROM sys.database_files WHERE type = 0;";
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<long> GetDatabaseUsedBytesAsync(CancellationToken ct = default)
    {
        // Used pages (not allocated file size) so the size-pressure loop terminates: this value drops as
        // rows are deleted, whereas sys.database_files.size never shrinks on DELETE.
        const string sql = "SELECT CAST(SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS BIGINT)) AS BIGINT) * 8 * 1024 FROM sys.database_files WHERE type = 0;";
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<bool> IsSizeCappedEditionAsync(CancellationToken ct = default)
    {
        // EngineEdition 4 = Express (the only edition with the 10 GB per-database data-file cap). 2/3 =
        // Standard/Enterprise, 5/8 = Azure SQL DB/MI — none capped, so size-pressure must not run there.
        const string sql = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT);";
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct)) == 4;
    }

    public async Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = (await cn.QueryAsync(new CommandDefinition(
            "SELECT TOP (@batch) * FROM relay_log ORDER BY logged_at ASC, id ASC;",
            new { batch }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return 0;

        // Archive first; if that throws, the rows are NOT deleted (the safety net must not lose data).
        await archive(rows.Select(r => ToRow((IDictionary<string, object>)r)).ToList(), ct);

        var ids = rows.Select(r => Convert.ToInt64(((IDictionary<string, object>)r)["id"])).ToArray();
        return await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM relay_log WHERE id IN @ids;", new { ids }, cancellationToken: ct));
    }

    // Materializes a Dapper row into a nullable-friendly read-only dictionary for archiving/serialization.
    internal static IReadOnlyDictionary<string, object?> ToRow(IDictionary<string, object> row) =>
        row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
