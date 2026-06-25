using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class BirdProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Bird,
        Settings = new Dictionary<string, string?>
        {
            ["ApiKey"] = "bird_secret",
            ["WorkspaceId"] = "ws-123",
            ["ChannelId"] = "ch-456",
        },
    };

    [Fact]
    public async Task Posts_to_channel_messages_with_accesskey_and_parses_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, "{\"id\":\"msg-789\"}");
        var result = await new BirdProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.bird.com/workspaces/ws-123/channels/ch-456/messages", handler.RequestUri);
        Assert.Equal("AccessKey bird_secret", handler.Header("Authorization"));
        Assert.Contains("emailaddress", handler.Body);
        Assert.Contains("rcpt@dest.com", handler.Body);
        Assert.Equal("msg-789", result.ProviderMessageId);
    }

    [Fact]
    public async Task Rate_limited_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.TooManyRequests, "slow down"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new BirdProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Auth_error_is_permanent()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized.\"}]}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BirdProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
