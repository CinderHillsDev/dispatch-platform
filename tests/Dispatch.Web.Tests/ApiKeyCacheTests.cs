using Dispatch.Core.ApiKeys;
using Dispatch.Web.Ingestion;

namespace Dispatch.Web.Tests;

public class ApiKeyCacheTests
{
    [Fact]
    public void Invalidate_evicts_the_revoked_key_immediately()
    {
        var cache = new ApiKeyCache();
        cache.Set("dsp_live_abc.secret", new ApiKey { Id = 7, KeyId = "dsp_live_abc", Name = "k" });
        Assert.NotNull(cache.Get("dsp_live_abc.secret"));

        cache.Invalidate(7);   // revoke → must stop working now, not after the TTL (spec §17.4)

        Assert.Null(cache.Get("dsp_live_abc.secret"));
    }

    [Fact]
    public void Invalidate_only_removes_the_matching_key_id()
    {
        var cache = new ApiKeyCache();
        cache.Set("raw1", new ApiKey { Id = 1 });
        cache.Set("raw2", new ApiKey { Id = 2 });

        cache.Invalidate(1);

        Assert.Null(cache.Get("raw1"));
        Assert.NotNull(cache.Get("raw2"));
    }
}
