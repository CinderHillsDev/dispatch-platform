using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class MailerooProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Maileroo,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "mlr_secret" },
    };

    [Fact]
    public async Task Posts_to_v2_emails_with_bearer_and_parses_reference_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK,
            "{\"success\":true,\"data\":{\"reference_id\":\"abc123\"}}");
        var result = await new MailerooProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://smtp.maileroo.com/api/v2/emails", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("mlr_secret", handler.AuthParameter);
        // from/to are address objects; recipient address is present.
        Assert.Contains("\"address\"", handler.Body);
        Assert.Contains("rcpt@dest.com", handler.Body);
        Assert.Equal("abc123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Rate_limited_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.TooManyRequests, "slow down"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new MailerooProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Auth_error_is_permanent()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.Unauthorized, "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MailerooProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
