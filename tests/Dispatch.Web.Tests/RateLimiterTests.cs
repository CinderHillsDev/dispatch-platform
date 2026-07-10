using Dispatch.Web.Ingestion;

namespace Dispatch.Web.Tests;

public class RateLimiterTests
{
    [Fact]
    public void Allows_up_to_limit_then_blocks()
    {
        var rl = new RateLimiter();
        Assert.True(rl.TryAcquire("key", 2));
        Assert.True(rl.TryAcquire("key", 2));
        Assert.False(rl.TryAcquire("key", 2));   // third within the same minute
    }

    [Fact]
    public void Zero_means_unlimited()
    {
        var rl = new RateLimiter();
        for (var i = 0; i < 1000; i++) Assert.True(rl.TryAcquire("k", 0));
    }

    [Fact]
    public void Keys_are_independent()
    {
        var rl = new RateLimiter();
        Assert.True(rl.TryAcquire("a", 1));
        Assert.False(rl.TryAcquire("a", 1));
        Assert.True(rl.TryAcquire("b", 1));   // separate bucket
    }
}
