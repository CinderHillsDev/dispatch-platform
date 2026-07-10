namespace Dispatch.Core.Counters;

/// <summary>
/// Always-written daily aggregates (spec §6.11). Backed by SQL <c>relay_counters</c> later;
/// in-memory for now. Increments must never throw into the relay path.
/// </summary>
public interface ICounterRepository
{
    Task IncrementAsync(int? relayId, CounterField field, CancellationToken ct = default);
}
