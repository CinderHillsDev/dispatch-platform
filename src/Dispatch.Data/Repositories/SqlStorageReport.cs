using Dapper;
using Dispatch.Core.Maintenance;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Computes database-side storage usage (spec §6.10): per-event relay_log row counts, the relay_log and
/// audit_log table sizes, and the audit row counts. Sizes come from the dialect — <c>pg_total_relation_size</c>
/// on Postgres, the dbstat module on SQLite (0 when that module isn't compiled in). Best-effort: if the
/// database is unreachable, returns a not-connected snapshot rather than throwing, so the dashboard storage
/// view degrades gracefully.
/// </summary>
public sealed class SqlStorageReport(SqlConnectionFactory factory) : IStorageReport
{
    public async Task<DbStorage> GetAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cn = await factory.OpenAsync(ct);

            var dbBytes = await factory.Dialect.GetDatabaseSizeBytesAsync(cn, ct);

            var byEvent = (await cn.QueryAsync<LogEventCount>(new CommandDefinition(
                "SELECT event AS \"Event\", count(*) AS \"Rows\" FROM relay_log GROUP BY event;",
                cancellationToken: ct))).ToList();

            var relayLogBytes = await factory.Dialect.GetTableSizeBytesAsync(cn, "relay_log", ct);
            var auditBytes = await factory.Dialect.GetTableSizeBytesAsync(cn, "audit_log", ct);

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
}
