using Dispatch.Core.Counters;
using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Always-written daily aggregates (spec §6.11). Never on the hot path for delivery, but every worker
/// thread writes here, and they all target the SAME (date, relay_id) row - which makes the increment the
/// most contended write in the system.
/// </summary>
public sealed class SqlCounterRepository(
    IDbContextFactory<DispatchDbContext> contexts, IDatabaseProvider provider) : ICounterRepository, ICounterReader
{
    /// <summary>
    /// Increments one counter column atomically.
    ///
    /// This is the one write that CANNOT be expressed as load-modify-save. Concurrent workers all bump the
    /// same row, so a read followed by a write would lose increments under exactly the load that matters.
    /// It has to be a single upsert statement, and every engine spells that differently (ON CONFLICT,
    /// MERGE, ON DUPLICATE KEY) - hence <see cref="IDatabaseProvider.CounterUpsertSql"/>.
    /// </summary>
    public async Task IncrementAsync(int? relayId, CounterField field, CancellationToken ct = default)
    {
        // relay_id 0 is the "no specific relay" bucket for connection-level events (denials counted before
        // routing). It is summed into the totals but excluded from per-relay views. Negative ids are invalid.
        var rid = relayId ?? 0;
        if (rid < 0) return;

        var column = field switch
        {
            CounterField.Received => "received",
            CounterField.Delivered => "delivered",
            CounterField.Failed => "failed",
            CounterField.Retried => "retried",
            CounterField.Denied => "denied",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        // The column name comes from the fixed enum mapping above, never from user input.
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            provider.CounterUpsertSql(column),
            [
                Parameter(db, "@date", DateOnly.FromDateTime(DateTime.UtcNow)),
                Parameter(db, "@relayId", rid),
            ], ct);
    }

    public async Task<CounterTotals> GetTodayAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await SumAsync(db.RelayCounters.AsNoTracking().Where(c => c.Date == today), ct);
    }

    public async Task<IReadOnlyList<RelayCounterTotals>> GetTodayByRelayAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var db = await contexts.CreateDbContextAsync(ct);

        // relay_id 0 is excluded so the denial bucket never appears as a phantom relay in per-relay views.
        return await db.RelayCounters.AsNoTracking()
            .Where(c => c.Date == today && c.RelayId > 0)
            .GroupBy(c => c.RelayId)
            .Select(g => new RelayCounterTotals(
                g.Key,
                g.Sum(c => c.Received), g.Sum(c => c.Delivered), g.Sum(c => c.Failed),
                g.Sum(c => c.Retried), g.Sum(c => c.Denied)))
            .ToListAsync(ct);
    }

    public async Task<CounterTotals> GetRangeTotalsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var (fromDate, toDate) = (DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtc));
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await SumAsync(db.RelayCounters.AsNoTracking().Where(c => c.Date >= fromDate && c.Date <= toDate), ct);
    }

    public async Task<IReadOnlyList<DailyCounterTotals>> GetDailyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var (fromDate, toDate) = (DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtc));
        await using var db = await contexts.CreateDbContextAsync(ct);

        var rows = await db.RelayCounters.AsNoTracking()
            .Where(c => c.Date >= fromDate && c.Date <= toDate)
            .GroupBy(c => c.Date)
            .Select(g => new
            {
                Date = g.Key,
                Received = g.Sum(c => c.Received),
                Delivered = g.Sum(c => c.Delivered),
                Failed = g.Sum(c => c.Failed),
                Retried = g.Sum(c => c.Retried),
                Denied = g.Sum(c => c.Denied),
            })
            .OrderBy(r => r.Date)
            .ToListAsync(ct);

        // Formatted here rather than in SQL: to_char / strftime / FORMAT / DATE_FORMAT are four different
        // spellings of the same thing, and none of them needs to happen in the database.
        return rows
            .Select(r => new DailyCounterTotals(
                r.Date.ToString("yyyy-MM-dd"), r.Received, r.Delivered, r.Failed, r.Retried, r.Denied))
            .ToList();
    }

    public async Task<IReadOnlyList<RelayReportRow>> GetRangeByRelayAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var (fromDate, toDate) = (DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtc));
        await using var db = await contexts.CreateDbContextAsync(ct);

        return await (from c in db.RelayCounters.AsNoTracking()
                      join r in db.Relays.AsNoTracking() on c.RelayId equals r.Id
                      where c.Date >= fromDate && c.Date <= toDate
                      group new { c, r } by new { c.RelayId, r.Name } into g
                      orderby g.Sum(x => x.c.Received) descending, g.Sum(x => x.c.Delivered) descending
                      select new RelayReportRow(
                          g.Key.RelayId, g.Key.Name,
                          g.Sum(x => x.c.Received), g.Sum(x => x.c.Delivered), g.Sum(x => x.c.Failed),
                          g.Sum(x => x.c.Retried), g.Sum(x => x.c.Denied)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Builds a parameter of whichever concrete type the active provider uses.
    ///
    /// The upsert is the one statement Dispatch still hands to the engine as raw SQL, so its parameters
    /// cannot be typed against any one client library. Asking the connection to create them keeps this
    /// provider-agnostic without the provider contract having to grow a parameter factory.
    /// </summary>
    private static System.Data.Common.DbParameter Parameter(DispatchDbContext db, string name, object value)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    /// <summary>
    /// Sums the five counter columns over a filtered set. Aggregated in the database, with the empty case
    /// collapsing to zeros rather than nulls.
    /// </summary>
    private static async Task<CounterTotals> SumAsync(IQueryable<RelayCounterEntity> query, CancellationToken ct)
    {
        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Received = g.Sum(c => c.Received),
                Delivered = g.Sum(c => c.Delivered),
                Failed = g.Sum(c => c.Failed),
                Retried = g.Sum(c => c.Retried),
                Denied = g.Sum(c => c.Denied),
            })
            .SingleOrDefaultAsync(ct);

        return totals is null
            ? new CounterTotals(0, 0, 0, 0, 0)
            : new CounterTotals(totals.Received, totals.Delivered, totals.Failed, totals.Retried, totals.Denied);
    }
}
