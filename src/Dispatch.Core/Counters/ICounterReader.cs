namespace Dispatch.Core.Counters;

/// <summary>Today's aggregate counters for the dashboard (spec §9.2 — read from <c>relay_counters</c>).</summary>
public sealed record CounterTotals(long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>Today's aggregate counters for a single relay.</summary>
public sealed record RelayCounterTotals(int RelayId, long Received, long Delivered, long Failed, long Retried, long Denied);

/// <summary>Read side of the daily counters, kept separate from the write-only <see cref="ICounterRepository"/>.</summary>
public interface ICounterReader
{
    Task<CounterTotals> GetTodayAsync(CancellationToken ct = default);

    /// <summary>Today's counters broken down per relay (for the dashboard's active-relay badges).</summary>
    Task<IReadOnlyList<RelayCounterTotals>> GetTodayByRelayAsync(CancellationToken ct = default);
}
