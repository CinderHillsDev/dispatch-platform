using System.Data;
using System.Text;
using Dapper;
using Dispatch.Core.Audit;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Repositories;

/// <summary>Append-only audit/security log (spec §17). Writes are best-effort: a logging failure is
/// swallowed (and warned) so it never breaks the audited action.</summary>
public sealed class SqlAuditLog(SqlConnectionFactory factory, ILogger<SqlAuditLog> log) : IAuditLog
{
    public async Task WriteAsync(string kind, string category, string @event, string severity,
        string? actor, string? sourceIp, string? detail, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO audit_log (kind, category, event, severity, actor, source_ip, detail)
            VALUES (@Kind, @Category, @Event, @Severity, @Actor, @SourceIp, @Detail);
            """;
        try
        {
            await using var cn = await factory.OpenAsync(ct);
            await cn.ExecuteAsync(new CommandDefinition(sql, new
            {
                Kind = Trunc(kind, 16),
                Category = Trunc(category, 32),
                Event = Trunc(@event, 128),
                Severity = Trunc(severity, 16),
                Actor = Trunc(actor, 128),
                SourceIp = Trunc(sourceIp, 64),
                Detail = detail,
            }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit write failed ({Category}/{Event})", category, @event);
        }
    }

    public async Task<AuditPage> QueryAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);
        var where = new StringBuilder("WHERE 1 = 1");
        var p = new DynamicParameters();
        p.Add("Limit", limit);

        if (!string.IsNullOrWhiteSpace(filter.Kind)) { where.Append(" AND kind = @Kind"); p.Add("Kind", filter.Kind); }
        if (!string.IsNullOrWhiteSpace(filter.Category)) { where.Append(" AND category = @Category"); p.Add("Category", filter.Category); }
        if (!string.IsNullOrWhiteSpace(filter.Severity)) { where.Append(" AND severity = @Severity"); p.Add("Severity", filter.Severity); }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Append(" AND (event LIKE @S ESCAPE '\\' OR detail LIKE @S ESCAPE '\\' OR actor LIKE @S ESCAPE '\\' OR category LIKE @S ESCAPE '\\')");
            var esc = filter.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            p.Add("S", "%" + esc + "%");
        }
        if (filter.Cursor is { } c)
        {
            where.Append(" AND (logged_at < @CursorAt OR (logged_at = @CursorAt AND id < @CursorId))");
            p.Add("CursorAt", c.LoggedAt, DbType.DateTime);
            p.Add("CursorId", c.Id);
        }

        var sql = $"""
            SELECT
                id AS Id, logged_at AS LoggedAt, kind AS Kind, category AS Category, event AS Event,
                severity AS Severity, actor AS Actor, source_ip AS SourceIp, detail AS Detail
            FROM audit_log
            {where}
            ORDER BY logged_at DESC, id DESC
            LIMIT @Limit;
            """;

        await using var cn = await factory.OpenAsync(ct);
        var rows = (await cn.QueryAsync<AuditEntry>(new CommandDefinition(sql, p, cancellationToken: ct))).ToList();
        var next = rows.Count == limit ? new AuditCursor(rows[^1].LoggedAt, rows[^1].Id) : null;
        return new AuditPage(rows, next);
    }

    public async Task<int> PurgeAsync(int generalRetentionDays, int securityRetentionDays, CancellationToken ct = default)
    {
        try
        {
            await using var cn = await factory.OpenAsync(ct);
            var total = 0;
            // Neither engine supports DELETE ... LIMIT portably, so each batch is bounded by a subquery on
            // the primary key. (The Postgres original used ctid; id is the PK on both engines and reads
            // the same either way.)
            var old = factory.Dialect.OlderThanDays("logged_at", "@Days");
            // Noisy security events (allow-list denials, SMTP auth failures) are kept shorter.
            if (securityRetentionDays > 0)
                total += await DeleteBatchedAsync(cn,
                    $"DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log WHERE category IN ('Access','SmtpAuth') AND {old} LIMIT @Batch);",
                    securityRetentionDays, ct);
            if (generalRetentionDays > 0)
                total += await DeleteBatchedAsync(cn,
                    $"DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log WHERE {old} LIMIT @Batch);",
                    generalRetentionDays, ct);
            return total;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit log purge failed");
            return 0;
        }
    }

    public async Task<int> ArchiveAndDeleteOldestAsync(int batch, Dispatch.Core.Maintenance.ArchiveRows archive, CancellationToken ct = default)
    {
        try
        {
            await using var cn = await factory.OpenAsync(ct);
            var rows = (await cn.QueryAsync(new CommandDefinition(
                "SELECT * FROM audit_log ORDER BY logged_at ASC, id ASC LIMIT @batch;",
                new { batch }, cancellationToken: ct))).ToList();
            if (rows.Count == 0) return 0;

            // Archive before deleting; if archiving throws, keep the rows.
            await archive(rows.Select(r => SqlLogMaintenance.ToRow((IDictionary<string, object>)r)).ToList(), ct);

            var ids = rows.Select(r => Convert.ToInt64(((IDictionary<string, object>)r)["id"])).ToArray();
            return await cn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM audit_log WHERE id IN @ids;", new { ids }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit archive-and-delete failed");
            return 0;
        }
    }

    private static async Task<int> DeleteBatchedAsync(System.Data.Common.DbConnection cn, string sql, int days, CancellationToken ct)
    {
        const int batch = 1000;
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var deleted = await cn.ExecuteAsync(new CommandDefinition(sql, new { Batch = batch, Days = days }, cancellationToken: ct));
            total += deleted;
            if (deleted < batch) break;
            await Task.Delay(100, ct);
        }
        return total;
    }

    private static string? Trunc(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
