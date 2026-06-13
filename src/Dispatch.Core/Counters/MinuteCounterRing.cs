namespace Dispatch.Core.Counters;

/// <summary>
/// In-memory 60-bucket ring of deliveries-per-minute for the dashboard throughput sparkline
/// (spec §9.2). Updated on every delivery regardless of log suppression settings.
/// </summary>
public sealed class MinuteCounterRing
{
    private readonly int[] _buckets = new int[60];
    private readonly Lock _lock = new();
    private long _lastBucketMinute = -1;

    public void Increment()
    {
        lock (_lock)
        {
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var idx = (int)(minute % 60);
            if (minute != _lastBucketMinute)
            {
                _buckets[idx] = 0;        // entering a new minute — reset stale bucket
                _lastBucketMinute = minute;
            }
            _buckets[idx]++;
        }
    }

    /// <summary>Snapshot of the 60 buckets (index = minute mod 60).</summary>
    public int[] Snapshot()
    {
        lock (_lock) return (int[])_buckets.Clone();
    }
}
