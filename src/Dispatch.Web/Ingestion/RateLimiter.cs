using System.Collections.Concurrent;

namespace Dispatch.Web.Ingestion;

/// <summary>Per-key fixed-window rate limiter (spec §7.3). In-memory; approximate under heavy contention.</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _buckets = new();

    public bool TryAcquire(string key, int perMinute)
    {
        if (perMinute <= 0) return true;   // 0 = unlimited

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var window = _buckets.AddOrUpdate(
            key,
            _ => new Window(minute, 1),
            (_, cur) => cur.Minute == minute ? cur with { Count = cur.Count + 1 } : new Window(minute, 1));

        return window.Count <= perMinute;
    }

    private readonly record struct Window(long Minute, int Count);
}
