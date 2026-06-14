using System.Collections.Concurrent;

namespace Dispatch.Service;

/// <summary>
/// Per-source-IP SMTP AUTH brute-force lockout (spec §17.10): 5 failed AUTH attempts within the window lock
/// that IP out for 60 seconds, during which AUTH is refused without touching the credential store. In-memory;
/// a successful AUTH clears the IP's failure history. Stale entries are pruned opportunistically so the map
/// can't grow unbounded. Mirrors the web dashboard's <c>LoginThrottle</c> with the SMTP policy.
/// </summary>
public sealed class SmtpAuthThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _byIp = new();
    private readonly Lock _lock = new();

    private sealed class Entry
    {
        public readonly List<DateTime> Failures = [];
        public DateTime? LockedUntilUtc;
    }

    /// <summary>True if the IP is currently locked out (AUTH should be refused without checking credentials).</summary>
    public bool IsLocked(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_byIp.TryGetValue(ip, out var e) || e.LockedUntilUtc is not { } until) return false;
            if (until <= now) { _byIp.TryRemove(ip, out _); return false; }
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
