using System.Data;
using System.Text;
using System.Text.Json;
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
        AppendFilters(where, p, filter);

        if (filter.Cursor is { } cursor)
        {
            where.Append(" AND (logged_at < @CursorLoggedAt OR (logged_at = @CursorLoggedAt AND id < @CursorId))");
            p.Add("CursorLoggedAt", cursor.LoggedAt, DbType.DateTime2);
            p.Add("CursorId", cursor.Id);
        }

        var sql = $"""
            SELECT TOP (@Limit)
                {RowColumns}
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

    public async Task<MessageLogPaged> PageAsync(MessageLogFilter filter, int offset, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);
        offset = Math.Max(0, offset);
        var where = new StringBuilder("WHERE 1 = 1");
        var p = new DynamicParameters();
        AppendFilters(where, p, filter);
        p.Add("Offset", offset);
        p.Add("Limit", limit);

        var sql = $"""
            SELECT COUNT(*) FROM relay_log {where};
            SELECT {RowColumns}
            FROM relay_log
            {where}
            ORDER BY logged_at DESC, id DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;

        await using var cn = await factory.OpenAsync(ct);
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var total = await multi.ReadSingleAsync<int>();
        var rows = (await multi.ReadAsync<MessageLogRow>()).ToList();
        return new MessageLogPaged(rows, total);
    }

    private const string RowColumns = """
        id AS Id, logged_at AS LoggedAt, event AS Event, status AS Status, spool_id AS SpoolId,
        from_address AS FromAddress, to_domain AS ToDomain, to_addresses AS ToAddressesJson,
        subject AS Subject, relay_name AS RelayName,
        provider AS Provider, duration_ms AS DurationMs, size_bytes AS SizeBytes,
        ingest_source AS IngestSource, retry_attempt AS RetryAttempt, error AS Error
        """;

    // Shared filter clauses (no cursor/paging). Every user value is a parameter — never interpolated.
    private static void AppendFilters(StringBuilder where, DynamicParameters p, MessageLogFilter filter)
    {
        // Bind dates as datetime2 to match the column precision.
        if (filter.FromUtc is { } from) { where.Append(" AND logged_at >= @FromUtc"); p.Add("FromUtc", from, DbType.DateTime2); }
        if (filter.ToUtc is { } to) { where.Append(" AND logged_at < @ToUtc"); p.Add("ToUtc", to, DbType.DateTime2); }
        if (filter.Statuses is { Length: > 0 } s) { where.Append(" AND status IN @Statuses"); p.Add("Statuses", s); }
        if (filter.Events is { Length: > 0 } ev) { where.Append(" AND event IN @Events"); p.Add("Events", ev); }
        if (!string.IsNullOrWhiteSpace(filter.IngestSource)) { where.Append(" AND ingest_source = @IngestSource"); p.Add("IngestSource", filter.IngestSource); }
        if (!string.IsNullOrWhiteSpace(filter.FromDomain)) { where.Append(" AND from_domain = @FromDomain"); p.Add("FromDomain", filter.FromDomain); }
        if (!string.IsNullOrWhiteSpace(filter.ToDomain)) { where.Append(" AND to_domain = @ToDomain"); p.Add("ToDomain", filter.ToDomain); }
        if (!string.IsNullOrWhiteSpace(filter.RelayName)) { where.Append(" AND relay_name = @RelayName"); p.Add("RelayName", filter.RelayName); }
        if (!string.IsNullOrWhiteSpace(filter.RoutingRuleName)) { where.Append(" AND routing_rule_name = @RoutingRuleName"); p.Add("RoutingRuleName", filter.RoutingRuleName); }
        // Subject substring match. Escape LIKE wildcards in the user value so they're treated literally.
        if (!string.IsNullOrWhiteSpace(filter.Subject))
        {
            where.Append(" AND subject LIKE @SubjectPattern ESCAPE '\\'");
            p.Add("SubjectPattern", "%" + EscapeLike(filter.Subject) + "%");
        }
        if (filter.ApiKeyId is { } apiKeyId) { where.Append(" AND api_key_id = @ApiKeyId"); p.Add("ApiKeyId", apiKeyId); }
        // Tag: tags is a JSON array string; match %"tag"% with the value as a parameter. Escape LIKE wildcards
        // so a tag containing % or _ matches literally (parameterised, so never an injection risk regardless).
        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            where.Append(" AND tags LIKE @TagPattern ESCAPE '\\'");
            p.Add("TagPattern", "%\"" + EscapeLike(filter.Tag) + "\"%");
        }
    }

    // Escapes the SQL LIKE metacharacters (\, %, _) so a user value is matched literally (used with ESCAPE '\').
    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, int? apiKeyId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1
                id AS Id, logged_at AS LoggedAt, event AS Event, status AS Status, spool_id AS SpoolId,
                from_address AS FromAddress, to_domain AS ToDomain, subject AS Subject, relay_name AS RelayName,
                provider AS Provider, duration_ms AS DurationMs, size_bytes AS SizeBytes,
                ingest_source AS IngestSource, retry_attempt AS RetryAttempt, error AS Error
            FROM relay_log
            WHERE spool_id = @spoolId AND (@apiKeyId IS NULL OR api_key_id = @apiKeyId)
            ORDER BY logged_at DESC, id DESC;
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<MessageLogRow>(
            new CommandDefinition(sql, new { spoolId, apiKeyId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MessageLogRow>> RecentByApiKeyAsync(
        int apiKeyId, int limit, string[]? statuses, CancellationToken ct = default)
    {
        var page = await QueryAsync(new MessageLogFilter
        {
            ApiKeyId = apiKeyId,
            Statuses = statuses is { Length: > 0 } ? statuses : null,
            Limit = limit,
        }, ct);
        return page.Rows;
    }

    public async Task<MessageLogDetail?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1
                id AS Id, logged_at AS LoggedAt, event AS Event, status AS Status, spool_id AS SpoolId,
                retry_attempt AS RetryAttempt, from_address AS FromAddress, from_domain AS FromDomain,
                to_addresses AS ToAddressesJson, to_domain AS ToDomain, subject AS Subject, size_bytes AS SizeBytes,
                relay_name AS RelayName, routing_rule_name AS RoutingRuleName, routing_matched AS RoutingMatched,
                provider AS Provider, provider_message_id AS ProviderMessageId, provider_response AS ProviderResponse,
                duration_ms AS DurationMs, error AS Error, ingest_source AS IngestSource, source_ip AS SourceIp,
                api_key_name AS ApiKeyName, tags AS TagsJson, x_mailer AS XMailer, attachment_count AS AttachmentCount
            FROM relay_log
            WHERE id = @id;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var raw = await cn.QuerySingleOrDefaultAsync<DetailRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
        if (raw is null) return null;

        // Retry/attempt timeline (spec §9.2): all relay_log rows sharing this spool id, oldest first.
        // Denied/pre-DATA rows have an empty spool id, so fall back to this single row as the history.
        IReadOnlyList<MessageLogAttempt> history;
        if (string.IsNullOrEmpty(raw.SpoolId))
        {
            history = [new MessageLogAttempt(raw.LoggedAt, raw.Event, raw.Status, raw.RetryAttempt, raw.Provider, raw.DurationMs, raw.Error)];
        }
        else
        {
            const string historySql = """
                SELECT logged_at AS LoggedAt, event AS Event, status AS Status, retry_attempt AS RetryAttempt,
                       provider AS Provider, duration_ms AS DurationMs, error AS Error
                FROM relay_log
                WHERE spool_id = @spoolId
                ORDER BY logged_at ASC, id ASC;
                """;
            history = (await cn.QueryAsync<MessageLogAttempt>(
                new CommandDefinition(historySql, new { spoolId = raw.SpoolId }, cancellationToken: ct))).ToList();
        }
        return raw.ToDetail(history);
    }

    /// <summary>Flat projection — the JSON array columns are deserialised into string[] by <see cref="ToDetail"/>.</summary>
    private sealed class DetailRow
    {
        public long Id { get; init; }
        public DateTime LoggedAt { get; init; }
        public string Event { get; init; } = "";
        public string Status { get; init; } = "";
        public string SpoolId { get; init; } = "";
        public int RetryAttempt { get; init; }
        public string FromAddress { get; init; } = "";
        public string FromDomain { get; init; } = "";
        public string? ToAddressesJson { get; init; }
        public string ToDomain { get; init; } = "";
        public string? Subject { get; init; }
        public int SizeBytes { get; init; }
        public string? RelayName { get; init; }
        public string? RoutingRuleName { get; init; }
        public bool RoutingMatched { get; init; }
        public string? Provider { get; init; }
        public string? ProviderMessageId { get; init; }
        public string? ProviderResponse { get; init; }
        public int? DurationMs { get; init; }
        public string? Error { get; init; }
        public string IngestSource { get; init; } = "";
        public string? SourceIp { get; init; }
        public string? ApiKeyName { get; init; }
        public string? TagsJson { get; init; }
        public string? XMailer { get; init; }
        public int AttachmentCount { get; init; }

        public MessageLogDetail ToDetail(IReadOnlyList<MessageLogAttempt> history) => new()
        {
            Id = Id, LoggedAt = LoggedAt, Event = Event, Status = Status, SpoolId = SpoolId,
            RetryAttempt = RetryAttempt, FromAddress = FromAddress, FromDomain = FromDomain,
            ToAddresses = ParseJsonArray(ToAddressesJson), ToDomain = ToDomain, Subject = Subject,
            SizeBytes = SizeBytes, RelayName = RelayName, RoutingRuleName = RoutingRuleName,
            RoutingMatched = RoutingMatched, Provider = Provider, ProviderMessageId = ProviderMessageId,
            ProviderResponse = ProviderResponse, DurationMs = DurationMs, Error = Error,
            IngestSource = IngestSource, SourceIp = SourceIp, ApiKeyName = ApiKeyName,
            Tags = ParseJsonArray(TagsJson), XMailer = XMailer, AttachmentCount = AttachmentCount, History = history,
        };
    }

    private static IReadOnlyList<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
