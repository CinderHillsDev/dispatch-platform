using Dapper;
using Dispatch.Core.Maintenance;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Computes database-side storage usage (spec §6.10): per-event relay_log row counts, the relay_log and
/// audit_log table sizes, and the audit row counts. Table sizes come from sys.allocation_units (the same
/// figures sp_spaceused reports). Best-effort: if SQL is unreachable, returns a not-connected snapshot
/// rather than throwing, so the dashboard storage view degrades gracefully.
/// </summary>
public sealed class SqlStorageReport(SqlConnectionFactory factory) : IStorageReport
{
    public async Task<DbStorage> GetAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cn = await factory.OpenAsync(ct);

            var dbBytes = await cn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT CAST(SUM(size) AS BIGINT) * 8 * 1024 FROM sys.database_files WHERE type = 0;",
                cancellationToken: ct));

            var byEvent = (await cn.QueryAsync<LogEventCount>(new CommandDefinition(
                "SELECT event AS [Event], COUNT_BIG(*) AS Rows FROM relay_log GROUP BY event;",
                cancellationToken: ct))).ToList();

            var relayLogBytes = await TableBytesAsync(cn, "relay_log", ct);
            var auditBytes = await TableBytesAsync(cn, "audit_log", ct);

            var audit = await cn.QuerySingleAsync<(long Total, long Security)>(new CommandDefinition(
                """
                SELECT COUNT_BIG(*) AS Total,
                       ISNULL(SUM(CASE WHEN category IN ('Access','SmtpAuth') THEN 1 ELSE 0 END), 0) AS Security
                FROM audit_log;
                """,
                cancellationToken: ct));

            return new DbStorage(true, dbBytes, relayLogBytes, byEvent, auditBytes, audit.Total, audit.Security);
        }
        catch
        {
            return new DbStorage(false, 0, 0, [], 0, 0, 0);
        }
    }

    // Total allocated bytes for a table (data + indexes), from the allocation-unit page counts (8 KB pages).
    private static async Task<long> TableBytesAsync(System.Data.Common.DbConnection cn, string table, CancellationToken ct) =>
        await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT ISNULL(SUM(a.total_pages), 0) * 8 * 1024
            FROM sys.tables t
            JOIN sys.indexes i ON t.object_id = i.object_id
            JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
            JOIN sys.allocation_units a ON p.partition_id = a.container_id
            WHERE t.name = @table;
            """,
            new { table }, cancellationToken: ct));
}
