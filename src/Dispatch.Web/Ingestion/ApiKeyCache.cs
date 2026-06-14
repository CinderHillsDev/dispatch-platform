using System.Collections.Concurrent;
using Dispatch.Core.ApiKeys;

namespace Dispatch.Web.Ingestion;

/// <summary>
/// Short-lived (30 s) cache of verified API keys keyed by the raw token (spec §7.7), so a valid key under
/// steady load doesn't pay a bcrypt compare on every request. Revoking a key calls <see cref="Invalidate"/>
/// so it stops working immediately (spec §17.4); otherwise entries simply expire after the TTL. In-memory;
/// stale entries are pruned opportunistically.
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

    /// <summary>Evict any cached entry for the given key id so a revoked key stops working immediately,
    /// not after the TTL (spec §17.4 — revocation has no grace period).</summary>
    public void Invalidate(int keyId)
    {
        foreach (var (raw, e) in _cache)
            if (e.Key.Id == keyId) _cache.TryRemove(raw, out _);
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
