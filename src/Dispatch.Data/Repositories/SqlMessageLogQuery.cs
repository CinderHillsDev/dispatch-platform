using System.Text.Json;
using Dispatch.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Keyset-paginated reads of <c>relay_log</c> for the Message Log (spec §9.2, §19). No <c>COUNT(*)</c> and
/// no <c>OFFSET</c> on the primary list path.
///
/// Filters are composed as LINQ predicates, so every user value is a parameter by construction rather than
/// by discipline - there is no string of SQL for an injection payload to reach. The SQL-injection tests in
/// the suite (§17) exercise that.
/// </summary>
public sealed class SqlMessageLogQuery(IDbContextFactory<DispatchDbContext> contexts) : IMessageLogQuery
{
    public async Task<MessageLogPage> QueryAsync(MessageLogFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = ApplyFilters(db.RelayLog.AsNoTracking(), filter);

        // Keyset pagination: seek past the last row seen instead of counting rows to skip, so page N costs
        // the same as page 1 regardless of how much history exists. The id tie-break makes the ordering
        // total when two rows share a timestamp.
        if (filter.Cursor is { } cursor)
            query = query.Where(r => r.LoggedAt < cursor.LoggedAt
                                  || (r.LoggedAt == cursor.LoggedAt && r.Id < cursor.Id));

        var rows = await query
            .OrderByDescending(r => r.LoggedAt).ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(RowProjection)
            .ToListAsync(ct);

        var next = rows.Count == limit ? new MessageLogCursor(rows[^1].LoggedAt, rows[^1].Id) : null;
        return new MessageLogPage(rows, next);
    }

    public async Task<MessageLogPaged> PageAsync(MessageLogFilter filter, int offset, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);
        offset = Math.Max(0, offset);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var filtered = ApplyFilters(db.RelayLog.AsNoTracking(), filter);

        // Count is a separate round trip, but a cheap one: counting the deduped id set is an index-only
        // aggregate (single-digit to low-tens of ms even at 100k messages, flat across the page offset).
        var total = await LatestPerMessage(filtered).CountAsync(ct);

        var rows = await LatestRows(filtered)
            .OrderByDescending(r => r.LoggedAt).ThenByDescending(r => r.Id)
            .Skip(offset).Take(limit)
            .Select(RowProjection)
            .ToListAsync(ct);

        return new MessageLogPaged(rows, total);
    }

    public async Task<IReadOnlyList<MessageLogRow>> RecentByApiKeyAsync(
        int apiKeyId, int limit, string[]? statuses, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var filtered = db.RelayLog.AsNoTracking().Where(r => r.ApiKeyId == apiKeyId);
        if (statuses is { Length: > 0 }) filtered = filtered.Where(r => statuses.Contains(r.Status));

        return await LatestRows(filtered)
            .OrderByDescending(r => r.LoggedAt).ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(RowProjection)
            .ToListAsync(ct);
    }

    public async Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, int? apiKeyId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.RelayLog.AsNoTracking().Where(r => r.SpoolId == spoolId);
        if (apiKeyId is { } key) query = query.Where(r => r.ApiKeyId == key);

        return await query
            .OrderByDescending(r => r.LoggedAt).ThenByDescending(r => r.Id)
            .Select(RowProjection)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<MessageLogDetail?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var raw = await db.RelayLog.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (raw is null) return null;

        // Retry/attempt timeline (spec §9.2): every relay_log row sharing this spool id, oldest first.
        // Denied and pre-DATA rows have an empty spool id, so that single row IS its own history - querying
        // by "" would otherwise collect every unrelated connection-level row ever recorded.
        IReadOnlyList<MessageLogAttempt> history = string.IsNullOrEmpty(raw.SpoolId)
            ? [new MessageLogAttempt(raw.LoggedAt, raw.Event, raw.Status, raw.RetryAttempt, raw.Provider, raw.DurationMs, raw.Error)]
            : await db.RelayLog.AsNoTracking()
                .Where(r => r.SpoolId == raw.SpoolId)
                .OrderBy(r => r.LoggedAt).ThenBy(r => r.Id)
                .Select(r => new MessageLogAttempt(r.LoggedAt, r.Event, r.Status, r.RetryAttempt, r.Provider, r.DurationMs, r.Error))
                .ToListAsync(ct);

        return ToDetail(raw, history);
    }

    /// <summary>
    /// The id of the newest row for each message.
    ///
    /// The list shows ONE row per message, but relay_log holds a row per lifecycle event: a Retrying row
    /// per failed attempt, then a terminal Delivered/Failed. Connection-level rows (denials, pre-DATA
    /// failures) have no spool id and must each stay their own entry.
    ///
    /// Split into two arms, UNIONed, rather than one GROUP BY with a conditional key:
    ///   * messages (non-empty spool_id) - GROUP BY spool_id, taking MAX(id). ids increase monotonically,
    ///     so within one spool id the highest is the latest event (this replaces a ROW_NUMBER() window).
    ///   * anonymous rows (empty spool_id) - each id as-is; they are never grouped together.
    ///
    /// The reason for the split is performance, and it is exact, not approximate. The obvious single query,
    /// GROUP BY (spool_id, CASE WHEN spool_id='' THEN id ELSE 0), produces identical results - but the CASE
    /// makes the group key non-sargable, so IX_relay_log_spool_id (spool_id, logged_at, id) cannot serve it
    /// and the engine scans relay_log into a temp B-tree. On the default (broad) Message Log filter over a
    /// multi-million-row table that is seconds per page. Grouping by spool_id alone uses the index.
    ///
    /// The two arms produce disjoint ids (an id is anonymous XOR it belongs to one spool group), so the set
    /// has no duplicates - which is what lets <see cref="LatestRows"/> consume it with an inner JOIN.
    /// </summary>
    private static IQueryable<long> LatestPerMessage(IQueryable<RelayLogEntity> filtered)
    {
        var messages = filtered.Where(r => r.SpoolId != "")
            .GroupBy(r => r.SpoolId)
            .Select(g => g.Max(r => r.Id));

        var anonymous = filtered.Where(r => r.SpoolId == "").Select(r => r.Id);

        return messages.Concat(anonymous);
    }

    /// <summary>
    /// The full relay_log rows for the deduped set - the message-list projection source.
    ///
    /// This is a JOIN to <see cref="LatestPerMessage"/>, not <c>Where(id => latestIds.Contains(id))</c>.
    /// They return identical rows (the id set is duplicate-free, so the inner join is a one-to-one match),
    /// but the SQL the engines produce is not equivalent. The IN-subquery form paginates fine on SQLite,
    /// PostgreSQL and SQL Server, yet MySQL/MariaDB plans it as a DEPENDENT subquery re-evaluated per
    /// candidate row: measured at 100k messages it ran ~700 ms at offset 0, ~8 s at offset 500, and did not
    /// finish (30 s timeout) at offset 5000. Rendered as a JOIN, the dedup derived table is materialised
    /// once and the same query is a flat ~55 ms across every offset on all four engines - and roughly
    /// halves PostgreSQL's time as a bonus. See CrossEngineVolumeTests for the standing numbers.
    /// </summary>
    private static IQueryable<RelayLogEntity> LatestRows(IQueryable<RelayLogEntity> filtered) =>
        filtered.Join(LatestPerMessage(filtered), r => r.Id, id => id, (r, _) => r);

    /// <summary>
    /// Shared filter clauses. Composed rather than concatenated, so values cannot be interpolated even by
    /// accident.
    /// </summary>
    private static IQueryable<RelayLogEntity> ApplyFilters(IQueryable<RelayLogEntity> query, MessageLogFilter filter)
    {
        if (filter.FromUtc is { } from) query = query.Where(r => r.LoggedAt >= from);
        if (filter.ToUtc is { } to) query = query.Where(r => r.LoggedAt < to);
        if (filter.Statuses is { Length: > 0 } statuses) query = query.Where(r => statuses.Contains(r.Status));
        if (filter.Events is { Length: > 0 } events) query = query.Where(r => events.Contains(r.Event));
        if (!string.IsNullOrWhiteSpace(filter.IngestSource)) query = query.Where(r => r.IngestSource == filter.IngestSource);
        if (!string.IsNullOrWhiteSpace(filter.FromDomain)) query = query.Where(r => r.FromDomain == filter.FromDomain);
        if (!string.IsNullOrWhiteSpace(filter.ToDomain)) query = query.Where(r => r.ToDomain == filter.ToDomain);
        if (!string.IsNullOrWhiteSpace(filter.RelayName)) query = query.Where(r => r.RelayName == filter.RelayName);
        if (!string.IsNullOrWhiteSpace(filter.RoutingRuleName)) query = query.Where(r => r.RoutingRuleName == filter.RoutingRuleName);
        if (filter.ApiKeyId is { } apiKeyId) query = query.Where(r => r.ApiKeyId == apiKeyId);

        // Contains rather than a hand-built LIKE: EF escapes the user's % and _ per provider, so searching
        // for "50%" stays a literal search instead of silently becoming a wildcard. Case sensitivity is
        // normalised across engines (see docs/database.md), so this matches the same rows everywhere.
        if (!string.IsNullOrWhiteSpace(filter.Subject))
        {
            var subject = filter.Subject;
            query = query.Where(r => r.Subject.Contains(subject));
        }

        // tags is a JSON array string; matching on the quoted value keeps "prod" from matching "production".
        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var tag = "\"" + filter.Tag + "\"";
            query = query.Where(r => r.Tags != null && r.Tags.Contains(tag));
        }

        return query;
    }

    /// <summary>The list projection. Kept as one expression so every read path returns the same shape.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<RelayLogEntity, MessageLogRow>> RowProjection =
        r => new MessageLogRow
        {
            Id = r.Id,
            LoggedAt = r.LoggedAt,
            Event = r.Event,
            Status = r.Status,
            SpoolId = r.SpoolId,
            FromAddress = r.FromAddress,
            ToDomain = r.ToDomain,
            ToAddressesJson = r.ToAddresses,
            Subject = r.Subject,
            RelayName = r.RelayName,
            Provider = r.Provider,
            DurationMs = r.DurationMs,
            SizeBytes = r.SizeBytes,
            IngestSource = r.IngestSource,
            RetryAttempt = r.RetryAttempt,
            Error = r.Error,
        };

    private static MessageLogDetail ToDetail(RelayLogEntity r, IReadOnlyList<MessageLogAttempt> history) => new()
    {
        Id = r.Id, LoggedAt = r.LoggedAt, Event = r.Event, Status = r.Status, SpoolId = r.SpoolId,
        RetryAttempt = r.RetryAttempt, FromAddress = r.FromAddress, FromDomain = r.FromDomain,
        ToAddresses = ParseJsonArray(r.ToAddresses), ToDomain = r.ToDomain, Subject = r.Subject,
        SizeBytes = r.SizeBytes, RelayName = r.RelayName, RoutingRuleName = r.RoutingRuleName,
        RoutingMatched = r.RoutingMatched, Provider = r.Provider, ProviderMessageId = r.ProviderMessageId,
        ProviderResponse = r.ProviderResponse, DurationMs = r.DurationMs, Error = r.Error,
        IngestSource = r.IngestSource, SourceIp = r.SourceIp, ApiKeyName = r.ApiKeyName,
        Tags = ParseJsonArray(r.Tags), XMailer = r.XMailer, AttachmentCount = r.AttachmentCount,
        History = history,
    };

    private static IReadOnlyList<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
