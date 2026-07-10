using Dispatch.Service;

namespace Dispatch.Core.Tests;

public class SmtpAuthThrottleTests
{
    [Fact]
    public void Locks_out_after_five_failures()
    {
        var t = new SmtpAuthThrottle();
        const string ip = "203.0.113.5";

        Assert.False(t.IsLocked(ip));
        for (var i = 0; i < 4; i++) { t.RecordFailure(ip); Assert.False(t.IsLocked(ip)); }
        t.RecordFailure(ip);   // 5th failure

        Assert.True(t.IsLocked(ip));
    }

    [Fact]
    public void Success_clears_the_failure_count()
    {
        var t = new SmtpAuthThrottle();
        const string ip = "203.0.113.6";

        for (var i = 0; i < 4; i++) t.RecordFailure(ip);
        t.RecordSuccess(ip);
        t.RecordFailure(ip);   // would have been the 5th without the reset

        Assert.False(t.IsLocked(ip));
    }

    [Fact]
    public void Lockout_is_per_source_ip()
    {
        var t = new SmtpAuthThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure("10.0.0.1");

        Assert.True(t.IsLocked("10.0.0.1"));
        Assert.False(t.IsLocked("10.0.0.2"));
    }
}
