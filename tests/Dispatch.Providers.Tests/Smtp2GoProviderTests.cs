using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class Smtp2GoProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Smtp2Go,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "api-secret" },
    };

    [Fact]
    public async Task Posts_to_v3_send_with_key_in_body_and_parses_email_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"data\":{\"email_id\":\"sg-123\"}}");
        var result = await new Smtp2GoProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.smtp2go.com/v3/email/send", handler.RequestUri);
        Assert.Contains("\"api_key\":\"api-secret\"", handler.Body);   // key travels in the JSON body
        Assert.Contains("rcpt@dest.com", handler.Body);
        Assert.Equal("sg-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.InternalServerError, "boom"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new Smtp2GoProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Missing_api_key_is_permanent()
    {
        var cfg = new RelayConfig { Provider = RelayProviderType.Smtp2Go, Settings = new Dictionary<string, string?>() };
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Smtp2GoProvider(cfg, http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
