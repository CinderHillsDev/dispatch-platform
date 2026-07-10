using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class SparkPostProviderTests
{
    private static RelayConfig Config(string? region = null)
    {
        var s = new Dictionary<string, string?> { ["ApiKey"] = "sp-secret" };
        if (region is not null) s["Region"] = region;
        return new RelayConfig { Provider = RelayProviderType.SparkPost, Settings = s };
    }

    [Fact]
    public async Task Posts_raw_mime_to_us_transmissions_with_api_key_header_and_parses_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"results\":{\"id\":\"sp-123\"}}");
        var result = await new SparkPostProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.sparkpost.com/api/v1/transmissions", handler.RequestUri);
        Assert.Equal("sp-secret", handler.Header("Authorization"));   // raw key, no scheme
        Assert.Contains("email_rfc822", handler.Body);                // raw MIME content
        Assert.Equal("sp-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Eu_region_uses_eu_endpoint()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"results\":{\"id\":\"x\"}}");
        await new SparkPostProvider(Config("EU"), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);
        Assert.StartsWith("https://api.eu.sparkpost.com/", handler.RequestUri!);
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.BadGateway, "boom"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new SparkPostProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
