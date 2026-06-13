using Dispatch.Core.Counters;

namespace Dispatch.Core.Tests;

public class MinuteCounterRingTests
{
    [Fact]
    public void Tracks_received_and_delivered_in_window()
    {
        var ring = new MinuteCounterRing();
        ring.RecordReceived();
        ring.RecordReceived();
        ring.RecordReceived();
        ring.RecordDelivered();
        ring.RecordDelivered();

        Assert.Equal(3, ring.SumReceived(5));
        Assert.Equal(2, ring.SumDelivered(5));

        var snap = ring.Snapshot();
        Assert.Equal(60, snap.Length);
        Assert.Equal(2, snap[^1]);   // current (newest) minute holds the 2 deliveries
    }
}
