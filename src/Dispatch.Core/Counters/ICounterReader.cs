namespace Dispatch.Core.Counters;

/// <summary>Today's aggregate counters for the dashboard (spec §9.2 - read from <c>relay_counters</c>).</summary>
public sealed record CounterTotals(long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>Today's aggregate counters for a single relay.</summary>
public sealed record RelayCounterTotals(int RelayId, long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>One day's totals across all relays (Reports time series). Date is "yyyy-MM-dd".</summary>
public sealed record DailyCounterTotals(string Date, long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>A relay's totals over a date range, with its display name (Reports per-relay table).</summary>
public sealed record RelayReportRow(int RelayId, string RelayName, long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>Read side of the daily counters, kept separate from the write-only <see cref="ICounterRepository"/>.</summary>
public interface ICounterReader
{
    Task<CounterTotals> GetTodayAsync(CancellationToken ct = default);

    /// <summary>Today's counters broken down per relay (for the dashboard's active-relay badges).</summary>
    Task<IReadOnlyList<RelayCounterTotals>> GetTodayByRelayAsync(CancellationToken ct = default);

    /// <summary>Summed totals over an inclusive UTC date range (Reports).</summary>
    Task<CounterTotals> GetRangeTotalsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Per-day totals over an inclusive UTC date range, oldest first (Reports time series).</summary>
    Task<IReadOnlyList<DailyCounterTotals>> GetDailyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Per-relay totals over an inclusive UTC date range, busiest first (Reports per-relay table).</summary>
    Task<IReadOnlyList<RelayReportRow>> GetRangeByRelayAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
