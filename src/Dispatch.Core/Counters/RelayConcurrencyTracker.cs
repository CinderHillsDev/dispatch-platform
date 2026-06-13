using System.Collections.Concurrent;

namespace Dispatch.Core.Counters;

/// <summary>
/// Tracks how many dispatches are in flight per relay right now (spec §9.2 "In Flight"). Updated by the
/// worker pool around each dispatch; read by the dashboard. In-memory and lock-free.
/// </summary>
public sealed class RelayConcurrencyTracker
{
    private readonly ConcurrentDictionary<int, int> _inFlight = new();

    public void Increment(int relayId) => _inFlight.AddOrUpdate(relayId, 1, (_, v) => v + 1);

    public void Decrement(int relayId) => _inFlight.AddOrUpdate(relayId, 0, (_, v) => Math.Max(0, v - 1));

    public IReadOnlyDictionary<int, int> Snapshot() => new Dictionary<int, int>(_inFlight);

    public int InFlight(int relayId) => _inFlight.TryGetValue(relayId, out var v) ? v : 0;
}
