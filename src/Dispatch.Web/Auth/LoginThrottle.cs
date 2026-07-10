using System.Collections.Concurrent;

namespace Dispatch.Web.Auth;

/// <summary>
/// Per-source-IP web-UI login lockout (spec §17.3): 10 failed attempts within 5 minutes locks that IP out
/// for 15 minutes. In-memory; a successful login clears the IP's failure history. Stale entries are pruned
/// opportunistically so the map can't grow unbounded.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _byIp = new();
    private readonly Lock _lock = new();

    private sealed class Entry
    {
        public readonly List<DateTime> Failures = [];
        public DateTime? LockedUntilUtc;
    }

    /// <summary>True if the IP is currently locked out; <paramref name="retryAfter"/> is the remaining time.</summary>
    public bool IsLocked(string ip, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ip, out var e) || e.LockedUntilUtc is not { } until) return false;
            if (until <= now) { _byIp.TryRemove(ip, out _); return false; }
            retryAfter = until - now;
            return true;
        }
    }

    public void RecordFailure(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var e = _byIp.GetOrAdd(ip, _ => new Entry());
            e.Failures.RemoveAll(t => now - t > Window);
            e.Failures.Add(now);
            if (e.Failures.Count >= MaxFailures)
            {
                e.LockedUntilUtc = now + LockoutDuration;
                e.Failures.Clear();
            }
            PruneStale(now);
        }
    }

    public void RecordSuccess(string ip)
    {
        lock (_lock) { _byIp.TryRemove(ip, out _); }
    }

    private void PruneStale(DateTime now)
    {
        // Called under _lock. Drop entries with no recent failures and no active lockout.
        foreach (var (ip, e) in _byIp)
        {
            var lockedActive = e.LockedUntilUtc is { } u && u > now;
            var hasRecent = e.Failures.Count > 0 && now - e.Failures[^1] <= Window;
            if (!lockedActive && !hasRecent) _byIp.TryRemove(ip, out _);
        }
    }
}
