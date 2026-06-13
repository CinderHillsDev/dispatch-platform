using System.Collections.Concurrent;
using Dispatch.Core.ApiKeys;

namespace Dispatch.Web.Ingestion;

/// <summary>
/// Short-lived (30 s) cache of verified API keys keyed by the raw token (spec §7.7), so a valid key under
/// steady load doesn't pay a bcrypt compare on every request. A revoked/changed key keeps working for at most
/// the TTL — an accepted trade-off per the spec. In-memory; stale entries are pruned opportunistically.
/// </summary>
public sealed class ApiKeyCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (ApiKey Key, DateTime ExpiresUtc)> _cache = new();
    private long _lastSweepTicks;

    public ApiKey? Get(string raw)
    {
        if (_cache.TryGetValue(raw, out var e) && e.ExpiresUtc > DateTime.UtcNow) return e.Key;
        return null;
    }

    public void Set(string raw, ApiKey key)
    {
        _cache[raw] = (key, DateTime.UtcNow + Ttl);
        Sweep();
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        // At most once every TTL window, regardless of call rate.
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < Ttl.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last) return;
        foreach (var (k, v) in _cache)
            if (v.ExpiresUtc <= now) _cache.TryRemove(k, out _);
    }
}
