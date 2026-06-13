using System.Data;
using System.Text;
using Dapper;
using Dispatch.Core.Logging;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Keyset-paginated reads of <c>relay_log</c> for the Message Log (spec §9.2, §19). No <c>COUNT(*)</c>,
/// no <c>OFFSET</c>. The WHERE clause is built by appending fixed fragments; every value is a Dapper
/// parameter, never interpolated — verified against SQL-injection payloads in the tests (§17).
/// </summary>
public sealed class SqlMessageLogQuery(SqlConnectionFactory factory) : IMessageLogQuery
{
    public async Task<MessageLogPage> QueryAsync(MessageLogFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);
        var where = new StringBuilder("WHERE 1 = 1");
        var p = new DynamicParameters();
        p.Add("Limit", limit);

        // Bind dates as datetime2 to match the column precision (a default DateTime param maps to the
        // lower-precision SQL `datetime`, which breaks the keyset tie-break at high insert rates).
        if (filter.FromUtc is { } from) { where.Append(" AND logged_at >= @FromUtc"); p.Add("FromUtc", from, DbType.DateTime2); }
        if (filter.ToUtc is { } to) { where.Append(" AND logged_at < @ToUtc"); p.Add("ToUtc", to, DbType.DateTime2); }
        if (filter.Statuses is { Length: > 0 } s) { where.Append(" AND status IN @Statuses"); p.Add("Statuses", s); }
        if (!string.IsNullOrWhiteSpace(filter.IngestSource)) { where.Append(" AND ingest_source = @IngestSource"); p.Add("IngestSource", filter.IngestSource); }
        if (!string.IsNullOrWhiteSpace(filter.FromDomain)) { where.Append(" AND from_domain = @FromDomain"); p.Add("FromDomain", filter.FromDomain); }
        if (!string.IsNullOrWhiteSpace(filter.ToDomain)) { where.Append(" AND to_domain = @ToDomain"); p.Add("ToDomain", filter.ToDomain); }

        if (filter.Cursor is { } cursor)
        {
            where.Append(" AND (logged_at < @CursorLoggedAt OR (logged_at = @CursorLoggedAt AND id < @CursorId))");
            p.Add("CursorLoggedAt", cursor.LoggedAt, DbType.DateTime2);
            p.Add("CursorId", cursor.Id);
        }

        var sql = $"""
            SELECT TOP (@Limit)
                id AS Id, logged_at AS LoggedAt, event AS Event, status AS Status, spool_id AS SpoolId,
                from_address AS FromAddress, to_domain AS ToDomain, subject AS Subject, relay_name AS RelayName,
                provider AS Provider, duration_ms AS DurationMs, size_bytes AS SizeBytes,
                ingest_source AS IngestSource, retry_attempt AS RetryAttempt, error AS Error
            FROM relay_log
            {where}
            ORDER BY logged_at DESC, id DESC;
            """;

        await using var cn = await factory.OpenAsync(ct);
        var rows = (await cn.QueryAsync<MessageLogRow>(new CommandDefinition(sql, p, cancellationToken: ct))).ToList();

        MessageLogCursor? next = rows.Count == limit
            ? new MessageLogCursor(rows[^1].LoggedAt, rows[^1].Id)
            : null;

        return new MessageLogPage(rows, next);
    }

    public async Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1
                id AS Id, logged_at AS LoggedAt, event AS Event, status AS Status, spool_id AS SpoolId,
                from_address AS FromAddress, to_domain AS ToDomain, subject AS Subject, relay_name AS RelayName,
                provider AS Provider, duration_ms AS DurationMs, size_bytes AS SizeBytes,
                ingest_source AS IngestSource, retry_attempt AS RetryAttempt, error AS Error
            FROM relay_log
            WHERE spool_id = @spoolId
            ORDER BY logged_at DESC, id DESC;
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<MessageLogRow>(
            new CommandDefinition(sql, new { spoolId }, cancellationToken: ct));
    }
}
