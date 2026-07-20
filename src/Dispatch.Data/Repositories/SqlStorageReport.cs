using Dispatch.Core.Maintenance;
using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Computes database-side storage usage (spec §6.10): per-event relay_log row counts, the relay_log and
/// audit_log table sizes, and the audit row counts.
///
/// Sizes come from the provider, since every engine introspects them differently and SQLite often cannot
/// do it per table at all. Best-effort throughout: if the database is unreachable this returns a
/// not-connected snapshot rather than throwing, so the dashboard storage view degrades instead of erroring.
/// </summary>
public sealed class SqlStorageReport(
    IDbContextFactory<DispatchDbContext> contexts, IDatabaseProvider provider) : IStorageReport
{
    public async Task<DbStorage> GetAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);

            var dbBytes = await provider.GetDatabaseSizeBytesAsync(db, ct);

            var byEvent = await db.RelayLog.AsNoTracking()
                .GroupBy(r => r.Event)
                .Select(g => new LogEventCount(g.Key, g.LongCount()))
                .ToListAsync(ct);

            var relayLogBytes = await provider.GetTableSizeBytesAsync(db, "relay_log", ct);
            var auditBytes = await provider.GetTableSizeBytesAsync(db, "audit_log", ct);

            var auditTotal = await db.AuditLog.AsNoTracking().LongCountAsync(ct);
            var auditSecurity = await db.AuditLog.AsNoTracking()
                .LongCountAsync(a => a.Category == "Access" || a.Category == "SmtpAuth", ct);

            return new DbStorage(true, dbBytes, relayLogBytes, byEvent, auditBytes, auditTotal, auditSecurity);
        }
        catch
        {
            return new DbStorage(false, 0, 0, [], 0, 0, 0);
        }
    }
}
