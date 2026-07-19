using Dispatch.Core.Audit;
using Dispatch.Core.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Append-only audit/security log (spec §17). Writes are best-effort: a logging failure is swallowed (and
/// warned) so it never breaks the audited action.
/// </summary>
public sealed class SqlAuditLog(IDbContextFactory<DispatchDbContext> contexts, ILogger<SqlAuditLog> log) : IAuditLog
{
    private const int PurgeBatch = 1000;

    public async Task WriteAsync(string kind, string category, string @event, string severity,
        string? actor, string? sourceIp, string? detail, CancellationToken ct = default)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            db.AuditLog.Add(new AuditLogEntity
            {
                // Stamped here rather than by the column default, so every row has the same precision -
                // see SqlLogRepository for why that matters to ordering on SQLite.
                LoggedAt = DateTime.UtcNow,
                Kind = Trunc(kind, 16) ?? "",
                Category = Trunc(category, 32) ?? "",
                Event = Trunc(@event, 128) ?? "",
                Severity = Trunc(severity, 16) ?? "Info",
                Actor = Trunc(actor, 128),
                SourceIp = Trunc(sourceIp, 64),
                Detail = detail,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit write failed ({Category}/{Event})", category, @event);
        }
    }

    public async Task<AuditPage> QueryAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 200);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.AuditLog.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Kind)) query = query.Where(a => a.Kind == filter.Kind);
        if (!string.IsNullOrWhiteSpace(filter.Category)) query = query.Where(a => a.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Severity)) query = query.Where(a => a.Severity == filter.Severity);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Contains rather than a hand-built LIKE: EF escapes the user's % and _ itself, per provider,
            // so a search for "50%" stays a literal search instead of becoming a wildcard.
            var search = filter.Search;
            query = query.Where(a =>
                a.Event.Contains(search)
                || (a.Detail != null && a.Detail.Contains(search))
                || (a.Actor != null && a.Actor.Contains(search))
                || a.Category.Contains(search));
        }

        // Keyset pagination: seek past the last row seen rather than counting rows to skip, so page N costs
        // the same as page 1. The id tie-break makes the order total when timestamps collide.
        if (filter.Cursor is { } c)
            query = query.Where(a => a.LoggedAt < c.LoggedAt || (a.LoggedAt == c.LoggedAt && a.Id < c.Id));

        var rows = await query
            .OrderByDescending(a => a.LoggedAt).ThenByDescending(a => a.Id)
            .Take(limit)
            .Select(a => new AuditEntry(a.Id, a.LoggedAt, a.Kind, a.Category, a.Event, a.Severity, a.Actor, a.SourceIp, a.Detail))
            .ToListAsync(ct);

        var next = rows.Count == limit ? new AuditCursor(rows[^1].LoggedAt, rows[^1].Id) : null;
        return new AuditPage(rows, next);
    }

    public async Task<int> PurgeAsync(int generalRetentionDays, int securityRetentionDays, CancellationToken ct = default)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var total = 0;

            // Noisy security events (allow-list denials, SMTP auth failures) are kept for less time.
            if (securityRetentionDays > 0)
                total += await PurgeBatchedAsync(db, securityRetentionDays,
                    a => a.Category == "Access" || a.Category == "SmtpAuth", ct);

            if (generalRetentionDays > 0)
                total += await PurgeBatchedAsync(db, generalRetentionDays, _ => true, ct);

            return total;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit log purge failed");
            return 0;
        }
    }

    /// <summary>
    /// Deletes aged rows in bounded batches, pausing between them.
    ///
    /// The cutoff is computed here rather than in SQL, which removes the last need for engine-specific
    /// interval arithmetic. Batching matters most on SQLite, where a single unbounded DELETE would hold the
    /// one write lock for its whole duration and stall ingest behind it; the pause is what lets a writer in.
    /// </summary>
    private static async Task<int> PurgeBatchedAsync(
        DispatchDbContext db, int retentionDays,
        System.Linq.Expressions.Expression<Func<AuditLogEntity, bool>> scope, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            // Select then delete by key: ExecuteDelete cannot carry a row limit on every provider, and an
            // unbounded delete is exactly what the batching exists to avoid.
            var ids = await db.AuditLog.AsNoTracking()
                .Where(scope).Where(a => a.LoggedAt < cutoff)
                .OrderBy(a => a.Id)
                .Take(PurgeBatch)
                .Select(a => a.Id)
                .ToListAsync(ct);

            if (ids.Count == 0) break;

            total += await db.AuditLog.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < PurgeBatch) break;
            await Task.Delay(100, ct);
        }

        return total;
    }

    public async Task<int> ArchiveAndDeleteOldestAsync(int batch, ArchiveRows archive, CancellationToken ct = default)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var rows = await db.AuditLog.AsNoTracking()
                .OrderBy(a => a.LoggedAt).ThenBy(a => a.Id)
                .Take(batch)
                .ToListAsync(ct);

            if (rows.Count == 0) return 0;

            // Archive BEFORE deleting: if archiving throws, the rows stay. The safety net must not lose data.
            await archive(rows.Select(ToRow).ToList(), ct);

            var ids = rows.Select(r => r.Id).ToList();
            return await db.AuditLog.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Audit archive-and-delete failed");
            return 0;
        }
    }

    /// <summary>Column-named dictionary for the archive writer, which serialises whatever it is given.</summary>
    private static IReadOnlyDictionary<string, object?> ToRow(AuditLogEntity a) => new Dictionary<string, object?>
    {
        ["id"] = a.Id,
        ["logged_at"] = a.LoggedAt,
        ["kind"] = a.Kind,
        ["category"] = a.Category,
        ["event"] = a.Event,
        ["severity"] = a.Severity,
        ["actor"] = a.Actor,
        ["source_ip"] = a.SourceIp,
        ["detail"] = a.Detail,
    };

    private static string? Trunc(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
