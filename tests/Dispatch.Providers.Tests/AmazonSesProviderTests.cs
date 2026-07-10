using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class AmazonSesProviderTests
{
    // SES builds a real AWS SDK client internally, so we can't exercise the wire here; but the required-
    // settings validation runs before any AWS call and is worth pinning (a misconfigured relay must fail
    // permanently and clearly, never silently).
    [Theory]
    [InlineData(null, "secret", "us-east-1")]
    [InlineData("akid", null, "us-east-1")]
    [InlineData("akid", "secret", null)]
    public async Task Missing_required_setting_is_permanent(string? accessKey, string? secret, string? region)
    {
        var settings = new Dictionary<string, string?>();
        if (accessKey is not null) settings["AccessKeyId"] = accessKey;
        if (secret is not null) settings["SecretAccessKey"] = secret;
        if (region is not null) settings["Region"] = region;
        var cfg = new RelayConfig { Provider = RelayProviderType.AmazonSes, Settings = settings };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AmazonSesProvider(cfg).SendAsync(ProviderTestSupport.Message(), default));
    }
}
