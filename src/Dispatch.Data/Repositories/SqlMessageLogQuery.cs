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
        var latestIds = LatestPerMessage(filtered);

        var total = await latestIds.CountAsync(ct);

        var rows = await filtered
            .Where(r => latestIds.Contains(r.Id))
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

        var latestIds = LatestPerMessage(filtered);

        return await filtered
            .Where(r => latestIds.Contains(r.Id))
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
    /// failures) have no spool id and must each stay their own entry, hence the composite grouping key -
    /// (spool_id, 0) collapses a message's events together, while ("", id) keeps every anonymous row apart.
    ///
    /// This replaces a ROW_NUMBER() OVER (PARTITION BY ... ORDER BY logged_at DESC, id DESC) window query.
    /// MAX(id) selects the same row: ids are monotonically increasing, so within one spool id the highest
    /// id is always the most recent event. It also translates cleanly on all four engines, where LINQ's
    /// group-then-take-first does not.
    /// </summary>
    private static IQueryable<long> LatestPerMessage(IQueryable<RelayLogEntity> filtered) =>
        filtered
            .GroupBy(r => new { r.SpoolId, Anonymous = r.SpoolId == "" ? r.Id : 0L })
            .Select(g => g.Max(r => r.Id));

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
