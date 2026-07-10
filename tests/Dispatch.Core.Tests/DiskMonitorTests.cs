using Dispatch.Core.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Core.Tests;

public class DiskMonitorTests
{
    [Theory]
    [InlineData(IntakeState.SuspendBytes - 1, IntakeLevel.Suspended)]
    [InlineData(IntakeState.SuspendBytes, IntakeLevel.Throttled)]      // >= suspend, < throttle
    [InlineData(IntakeState.ThrottleBytes - 1, IntakeLevel.Throttled)]
    [InlineData(IntakeState.ThrottleBytes, IntakeLevel.Normal)]
    [InlineData(IntakeState.WarnBytes + 1, IntakeLevel.Normal)]
    public void Apply_maps_free_space_to_level(long freeBytes, IntakeLevel expected)
    {
        var state = new IntakeState();
        Assert.Equal(expected, state.Apply(freeBytes));
        Assert.Equal(expected, state.Level);
    }

    [Fact]
    public void Monitor_transitions_through_levels_as_free_space_changes()
    {
        using var t = new TempSpool();
        var state = new IntakeState();
        long free = IntakeState.WarnBytes * 2;   // start comfortably normal
        var monitor = new DiskMonitor(t.Spool, state, _ => free, NullLogger<DiskMonitor>.Instance);

        monitor.Evaluate();
        Assert.Equal(IntakeLevel.Normal, state.Level);

        free = IntakeState.ThrottleBytes - 1;    // drop below throttle threshold
        monitor.Evaluate();
        Assert.Equal(IntakeLevel.Throttled, state.Level);

        free = IntakeState.SuspendBytes - 1;     // drop below suspend threshold
        monitor.Evaluate();
        Assert.Equal(IntakeLevel.Suspended, state.Level);

        free = IntakeState.ThrottleBytes + 1;    // recover above throttle threshold
        monitor.Evaluate();
        Assert.Equal(IntakeLevel.Normal, state.Level);
    }

    [Fact]
    public void Monitor_leaves_level_unchanged_when_probe_throws()
    {
        using var t = new TempSpool();
        var state = new IntakeState();
        state.Apply(IntakeState.ThrottleBytes - 1);   // pre-set to Throttled

        var monitor = new DiskMonitor(t.Spool, state,
            _ => throw new IOException("drive unavailable"), NullLogger<DiskMonitor>.Instance);
        monitor.Evaluate();

        Assert.Equal(IntakeLevel.Throttled, state.Level);   // failure is swallowed, level preserved
    }
}
