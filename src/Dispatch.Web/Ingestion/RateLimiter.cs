using System.Collections.Concurrent;

namespace Dispatch.Web.Ingestion;

/// <summary>Per-key fixed-window rate limiter (spec §7.3). In-memory; approximate under heavy contention.</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _buckets = new();
    private long _lastSweptMinute = -1;

    public bool TryAcquire(string key, int perMinute)
    {
        if (perMinute <= 0) return true;   // 0 = unlimited

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        EvictStale(minute);
        var window = _buckets.AddOrUpdate(
            key,
            _ => new Window(minute, 1),
            (_, cur) => cur.Minute == minute ? cur with { Count = cur.Count + 1 } : new Window(minute, 1));

        return window.Count <= perMinute;
    }

    /// <summary>
    /// Drops windows from previous minutes so the bucket map can't grow unbounded as new keys/IPs appear
    /// (each entry is dead once its minute passes). Runs at most once per minute across all callers.
    /// </summary>
    private void EvictStale(long minute)
    {
        if (Interlocked.Exchange(ref _lastSweptMinute, minute) == minute) return;
        foreach (var kv in _buckets)
            if (kv.Value.Minute < minute)
                _buckets.TryRemove(kv.Key, out _);
    }

    private readonly record struct Window(long Minute, int Count);
}
