using Dispatch.Web.Auth;

namespace Dispatch.Web.Tests;

// Per-IP web-UI login lockout (brute-force defence). Pure in-memory logic — no host needed.
public class LoginThrottleTests
{
    [Fact]
    public void Nine_failures_does_not_lock_tenth_does()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 9; i++) t.RecordFailure("1.2.3.4");
        Assert.False(t.IsLocked("1.2.3.4", out _));

        t.RecordFailure("1.2.3.4");   // 10th
        Assert.True(t.IsLocked("1.2.3.4", out var retry));
        Assert.True(retry > TimeSpan.Zero);
    }

    [Fact]
    public void Success_clears_failure_history()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 9; i++) t.RecordFailure("1.2.3.4");
        t.RecordSuccess("1.2.3.4");
        // History cleared — a fresh burst must start the count over, not tip into lockout.
        for (var i = 0; i < 9; i++) t.RecordFailure("1.2.3.4");
        Assert.False(t.IsLocked("1.2.3.4", out _));
    }

    [Fact]
    public void Lockout_is_per_ip()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 10; i++) t.RecordFailure("1.1.1.1");
        Assert.True(t.IsLocked("1.1.1.1", out _));
        Assert.False(t.IsLocked("2.2.2.2", out _));   // a different IP is unaffected
    }

    [Fact]
    public void Unknown_ip_is_not_locked()
    {
        var t = new LoginThrottle();
        Assert.False(t.IsLocked("9.9.9.9", out var retry));
        Assert.Equal(TimeSpan.Zero, retry);
    }
}
