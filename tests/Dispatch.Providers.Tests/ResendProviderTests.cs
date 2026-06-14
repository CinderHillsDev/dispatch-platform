using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class ResendProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Resend,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "re_secret" },
    };

    [Fact]
    public async Task Posts_to_emails_endpoint_with_bearer_and_parses_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"id\":\"re-123\"}");
        var result = await new ResendProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.resend.com/emails", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("re_secret", handler.AuthParameter);
        Assert.Contains("rcpt@dest.com", handler.Body);
        Assert.Equal("re-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Rate_limited_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.TooManyRequests, "slow down"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new ResendProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Auth_error_is_permanent()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.Unauthorized, "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ResendProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
