using Dispatch.Core.Configuration;

namespace Dispatch.Core.Tests;

public class SmtpPortResolverTests
{
    // Helper: a canBind predicate that returns true only for the given "free" ports.
    private static Func<int, bool> Free(params int[] free) => p => free.Contains(p);

    [Fact]
    public void All_configured_ports_bindable_no_fallback()
    {
        var result = SmtpPortResolver.Resolve([25, 587], Free(25, 587, 2525));
        Assert.Equal([25, 587], result);
    }

    [Fact]
    public void Port25_taken_falls_back_to_2525_and_keeps_587()
    {
        // 25 unavailable (in use or no privilege), 587 + 2525 free.
        var result = SmtpPortResolver.Resolve([25, 587], Free(587, 2525));
        Assert.Equal([587, 2525], result);
    }

    [Fact]
    public void Nothing_privileged_bindable_falls_back_to_2525_only()
    {
        // Simulates an unprivileged run: neither 25 nor 587 can bind, but 2525 can.
        var result = SmtpPortResolver.Resolve([25, 587], Free(2525));
        Assert.Equal([2525], result);
    }

    [Fact]
    public void Nothing_bindable_returns_empty()
    {
        // Even 2525 is taken - the listener should report nothing (caller keeps the host alive).
        var result = SmtpPortResolver.Resolve([25, 587], Free(/* none */));
        Assert.Empty(result);
    }

    [Fact]
    public void Explicit_2525_config_does_not_duplicate_fallback()
    {
        // Operator configured only 2525; 25 was never requested so no fallback is added, and no dup.
        var result = SmtpPortResolver.Resolve([2525], Free(2525));
        Assert.Equal([2525], result);
    }

    [Fact]
    public void Port25_taken_but_2525_also_taken_keeps_only_587()
    {
        // 25 down, 2525 also unavailable, 587 free - no fallback possible, keep what bound.
        var result = SmtpPortResolver.Resolve([25, 587], Free(587));
        Assert.Equal([587], result);
    }

    [Fact]
    public void Warnings_emitted_for_dropped_ports_and_fallback()
    {
        var warnings = new List<string>();
        SmtpPortResolver.Resolve([25, 587], Free(587, 2525), warnings.Add);
        Assert.Contains(warnings, w => w.Contains("25") && w.Contains("unavailable"));
        Assert.Contains(warnings, w => w.Contains("2525") && w.Contains("Falling back"));
    }
}
