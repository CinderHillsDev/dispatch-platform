using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Dispatch.Web.Tests;

public class IngestionApiTests(WebTestHost host) : IClassFixture<WebTestHost>
{
    private HttpRequestMessage Post(string key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    [Fact]
    public async Task Health_is_open_without_auth()
    {
        var res = await host.Web.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_without_key_is_401()
    {
        var res = await host.Api.PostAsJsonAsync("/api/v1/messages",
            new { from = "a@x.com", to = new[] { "b@y.com" }, subject = "hi", text = "yo" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_with_invalid_key_is_401()
    {
        var res = await host.Api.SendAsync(Post("dsp_live_nope000000000000000000000000000", new { from = "a@x.com", to = new[] { "b@y.com" }, subject = "hi", text = "yo" }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_with_valid_key_returns_202_and_writes_spool()
    {
        var before = Directory.GetFiles(host.Spool.IncomingDir, "*.eml").Length;
        var res = await host.Api.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "App <a@x.com>", to = new[] { "b@y.com" }, subject = "Hello", html = "<b>hi</b>", tags = new[] { "welcome" } }));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AcceptedResponse>();
        Assert.StartsWith("spl_", body!.Id);

        var after = Directory.GetFiles(host.Spool.IncomingDir, "*.eml").Length;
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Ingest_missing_recipient_is_400()
    {
        var res = await host.Api.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "a@x.com", subject = "hi", text = "yo" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_without_body_content_is_400()
    {
        // Neither text nor html.
        var res = await host.Api.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "a@x.com", to = new[] { "b@y.com" }, subject = "hi" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_after_threshold()
    {
        // LimitedKey allows 2/min.
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var res = await host.Api.SendAsync(Post(WebTestHost.LimitedKey,
                new { from = "a@x.com", to = new[] { "b@y.com" }, subject = "hi", text = "yo" }));
            codes.Add(res.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Ingestion_route_is_not_exposed_on_web_port()
    {
        var res = await host.Web.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "a@x.com", to = new[] { "b@y.com" }, subject = "hi", text = "yo" }));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Stats_endpoint_returns_shape()
    {
        var res = await host.Web.GetAsync("/api/stats");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("delivered", json);
        Assert.Contains("spool", json);
    }

    private sealed record AcceptedResponse(string Id, string Message);
}
