namespace Dispatch.Core.Counters;

/// <summary>
/// In-memory per-minute rolling counters (last 60 minutes) for received and delivered (sent-to-provider)
/// messages. Drives the dashboard throughput sparkline (spec §9.2) and the rolling windows on /health.
/// Each bucket records which absolute minute it holds, so windowed sums ignore stale buckets.
/// </summary>
public sealed class MinuteCounterRing
{
    private readonly Ring _received = new();
    private readonly Ring _delivered = new();

    public void RecordReceived() => _received.Record();
    public void RecordDelivered() => _delivered.Record();

    /// <summary>Received in the last <paramref name="minutes"/> minutes.</summary>
    public int SumReceived(int minutes) => _received.Sum(minutes);

    /// <summary>Delivered to providers in the last <paramref name="minutes"/> minutes.</summary>
    public int SumDelivered(int minutes) => _delivered.Sum(minutes);

    /// <summary>Delivered-per-minute for the last 60 minutes, oldest→newest (sparkline).</summary>
    public int[] Snapshot() => _delivered.SnapshotChronological();

    private sealed class Ring
    {
        private const int Size = 60;
        private readonly long[] _minute = new long[Size];
        private readonly int[] _count = new int[Size];
        private readonly Lock _lock = new();

        public Ring() => Array.Fill(_minute, -1);

        private static long NowMinute() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

        public void Record()
        {
            var m = NowMinute();
            var i = (int)(m % Size);
            lock (_lock)
            {
                if (_minute[i] != m) { _minute[i] = m; _count[i] = 0; }   // reclaim a stale bucket
                _count[i]++;
            }
        }

        public int Sum(int minutes)
        {
            var now = NowMinute();
            var lo = now - Math.Max(0, minutes - 1);
            var total = 0;
            lock (_lock)
            {
                for (var i = 0; i < Size; i++)
                    if (_minute[i] >= lo && _minute[i] <= now) total += _count[i];
            }
            return total;
        }

        public int[] SnapshotChronological()
        {
            var now = NowMinute();
            var snap = new int[Size];
            lock (_lock)
            {
                for (var k = 0; k < Size; k++)
                {
                    var m = now - (Size - 1 - k);          // oldest first
                    var i = (int)(((m % Size) + Size) % Size);
                    snap[k] = _minute[i] == m ? _count[i] : 0;
                }
            }
            return snap;
        }
    }
}
