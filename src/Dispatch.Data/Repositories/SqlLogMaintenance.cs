using Dapper;
using Dispatch.Core.Logging;

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

    public async Task<int> PurgeOldestAsync(int batchSize, CancellationToken ct = default)
    {
        var sql = $"DELETE TOP ({batchSize}) FROM relay_log WHERE id IN (SELECT TOP ({batchSize}) id FROM relay_log ORDER BY logged_at ASC, id ASC);";
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
