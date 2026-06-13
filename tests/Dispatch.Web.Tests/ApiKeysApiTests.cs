using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class ApiKeysApiTests(WebTestHost host)
{
    private static HttpRequestMessage Get(string url, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    [Fact]
    public async Task Dashboard_keys_list_returns_shape()
    {
        var res = await host.Web.GetAsync("/api/keys");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("keyId", json);
        Assert.Contains("messageCount", json);
    }

    [Fact]
    public async Task Dashboard_keys_not_served_on_api_port()
    {
        // The api port is fronted by ApiKeyMiddleware; an authenticated request still must not
        // reach the web-port-only /api/keys route (the port filter yields 404 once auth passes).
        var res = await host.Api.SendAsync(Get("/api/keys", WebTestHost.ValidKey));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Per_key_message_list_requires_auth()
    {
        var res = await host.Api.GetAsync("/api/v1/messages");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Per_key_message_list_returns_rows_for_calling_key()
    {
        var res = await host.Api.SendAsync(Get("/api/v1/messages?limit=10", WebTestHost.ValidKey));
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<MessagesResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Messages);
        Assert.Equal("spool-7", body.Messages[0].SpoolId);
    }

    [Fact]
    public async Task Per_key_message_list_is_scoped_to_the_calling_key()
    {
        // OtherKey (id 3) has no messages in the fake — must not see ValidKey's row.
        var res = await host.Api.SendAsync(Get("/api/v1/messages", WebTestHost.OtherKey));
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<MessagesResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Messages);
    }

    [Fact]
    public async Task Per_key_message_list_not_exposed_on_web_port()
    {
        var res = await host.Web.SendAsync(Get("/api/v1/messages", WebTestHost.ValidKey));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    private sealed record MessagesResponse(List<MessageRow> Messages);
    private sealed record MessageRow(string SpoolId, string Status, string Event);
}
