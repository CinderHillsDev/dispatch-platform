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
}
