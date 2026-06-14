using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class PostmarkProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Postmark,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "server-token" },
    };

    [Fact]
    public async Task Posts_to_email_endpoint_with_server_token_and_parses_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"MessageID\":\"pm-123\",\"ErrorCode\":0}");
        var result = await new PostmarkProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.postmarkapp.com/email", handler.RequestUri);
        Assert.Equal("server-token", handler.Header("X-Postmark-Server-Token"));
        Assert.Contains("\"To\":\"rcpt@dest.com\"", handler.Body);
        Assert.Contains("\"MessageStream\":\"outbound\"", handler.Body);   // default stream
        Assert.Equal("pm-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Nonzero_error_code_on_http_200_is_permanent()
    {
        // Postmark returns HTTP 200 with a non-zero ErrorCode for permanent failures (e.g. inactive recipient).
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"ErrorCode\":406,\"Message\":\"inactive\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostmarkProvider(Config(), new HttpClient(handler)).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "down"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new PostmarkProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Missing_api_key_is_permanent()
    {
        var cfg = new RelayConfig { Provider = RelayProviderType.Postmark, Settings = new Dictionary<string, string?>() };
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostmarkProvider(cfg, http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
