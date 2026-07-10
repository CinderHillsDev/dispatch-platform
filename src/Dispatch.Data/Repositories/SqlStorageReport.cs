using Dapper;
using Dispatch.Core.Maintenance;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Computes database-side storage usage (spec §6.10): per-event relay_log row counts, the relay_log and
/// audit_log table sizes, and the audit row counts. Table sizes come from <c>pg_total_relation_size</c>
/// (data + indexes + TOAST). Best-effort: if the database is unreachable, returns a not-connected snapshot
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
                "SELECT pg_database_size(current_database());",
                cancellationToken: ct));

            var byEvent = (await cn.QueryAsync<LogEventCount>(new CommandDefinition(
                "SELECT event AS \"Event\", count(*) AS \"Rows\" FROM relay_log GROUP BY event;",
                cancellationToken: ct))).ToList();

            var relayLogBytes = await TableBytesAsync(cn, "relay_log", ct);
            var auditBytes = await TableBytesAsync(cn, "audit_log", ct);

            var audit = await cn.QuerySingleAsync<(long Total, long Security)>(new CommandDefinition(
                """
                SELECT count(*) AS Total,
                       COALESCE(SUM(CASE WHEN category IN ('Access','SmtpAuth') THEN 1 ELSE 0 END), 0) AS Security
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

    // Total on-disk bytes for a table (data + indexes + TOAST). Passing the name through to_regclass keeps
    // the lookup safe and returns NULL (→ 0) if the table doesn't exist yet.
    private static async Task<long> TableBytesAsync(System.Data.Common.DbConnection cn, string table, CancellationToken ct) =>
        await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(pg_total_relation_size(to_regclass(@table)), 0)::bigint;",
            new { table }, cancellationToken: ct));
}
