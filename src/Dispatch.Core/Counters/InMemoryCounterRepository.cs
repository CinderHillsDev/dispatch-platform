using System.Collections.Concurrent;

namespace Dispatch.Core.Counters;

/// <summary>In-memory counter store (used in tests and as a dashboard source without SQL).</summary>
public sealed class InMemoryCounterRepository : ICounterRepository, ICounterReader
{
    private readonly ConcurrentDictionary<(int RelayId, CounterField Field), long> _counts = new();

    public Task IncrementAsync(int? relayId, CounterField field, CancellationToken ct = default)
    {
        _counts.AddOrUpdate((relayId ?? 0, field), 1, (_, v) => v + 1);
        return Task.CompletedTask;
    }

    public long Get(int relayId, CounterField field) =>
        _counts.TryGetValue((relayId, field), out var v) ? v : 0;

    public Task<CounterTotals> GetTodayAsync(CancellationToken ct = default)
    {
        long Sum(CounterField f) => _counts.Where(kv => kv.Key.Field == f).Sum(kv => kv.Value);
        return Task.FromResult(new CounterTotals(
            Sum(CounterField.Received), Sum(CounterField.Delivered), Sum(CounterField.Failed),
            Sum(CounterField.Retried), Sum(CounterField.Denied)));
    }

    public Task<IReadOnlyList<RelayCounterTotals>> GetTodayByRelayAsync(CancellationToken ct = default)
    {
        long Get(int relay, CounterField f) => _counts.TryGetValue((relay, f), out var v) ? v : 0;
        var result = _counts.Keys.Select(k => k.RelayId).Distinct()
            .Select(r => new RelayCounterTotals(r,
                Get(r, CounterField.Received), Get(r, CounterField.Delivered), Get(r, CounterField.Failed),
                Get(r, CounterField.Retried), Get(r, CounterField.Denied)))
            .ToList();
        return Task.FromResult<IReadOnlyList<RelayCounterTotals>>(result);
    }

    // The in-memory store has no date dimension, so range queries collapse to the single bucket of totals.
    public Task<CounterTotals> GetRangeTotalsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => GetTodayAsync(ct);

    public async Task<IReadOnlyList<DailyCounterTotals>> GetDailyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var t = await GetTodayAsync(ct);
        return [new DailyCounterTotals(toUtc.ToString("yyyy-MM-dd"), t.Received, t.Delivered, t.Failed, t.Retried, t.Denied)];
    }

    public async Task<IReadOnlyList<RelayReportRow>> GetRangeByRelayAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await GetTodayByRelayAsync(ct);
        return rows.Select(r => new RelayReportRow(r.RelayId, $"relay-{r.RelayId}", r.Received, r.Delivered, r.Failed, r.Retried, r.Denied)).ToList();
    }
}
