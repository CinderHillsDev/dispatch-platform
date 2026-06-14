using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dispatch.Core.Spool;
using MimeKit;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class IngestionApiTests(WebTestHost host)
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
    public async Task Health_is_open_without_auth_and_reports_healthy_shape()
    {
        var res = await host.Web.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", doc.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("version").GetString()));
        Assert.True(doc.GetProperty("uptimeSeconds").GetInt64() >= 0);

        var spool = doc.GetProperty("spool");
        Assert.True(spool.TryGetProperty("incoming", out _));
        Assert.True(spool.TryGetProperty("diskFreeMb", out _));

        var sql = doc.GetProperty("sql");
        Assert.True(sql.GetProperty("connected").GetBoolean());
        Assert.Equal(142, sql.GetProperty("dbSizeMb").GetInt64());

        var smtp = doc.GetProperty("smtp");
        Assert.True(smtp.GetProperty("listening").GetBoolean());
        Assert.NotEmpty(smtp.GetProperty("ports").EnumerateArray());
    }

    [Fact]
    public async Task Health_returns_503_critical_when_intake_suspended()
    {
        host.Intake.Apply(0);   // below SuspendBytes -> IntakeLevel.Suspended
        try
        {
            var res = await host.Web.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);

            var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("critical", doc.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(doc.GetProperty("message").GetString()));
        }
        finally
        {
            host.Intake.Apply(long.MaxValue);   // restore Normal for the shared host
        }
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
    public async Task Ingest_multipart_writes_spool_with_meta_and_tags()
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("App <a@x.com>"), "from" },
            { new StringContent("b@y.com"), "to" },
            { new StringContent("Multipart hi"), "subject" },
            { new StringContent("hello"), "text" },
            { new StringContent("welcome"), "o:tag" },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WebTestHost.ValidKey);

        var res = await host.Api.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var newest = new DirectoryInfo(host.Spool.IncomingDir).GetFiles("*.eml")
            .OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
        var meta = SpoolMeta.Load(newest);
        Assert.Equal("API", meta.IngestSource);
        Assert.Equal("a@x.com", meta.FromAddress);
        Assert.Contains("welcome", meta.Tags!);
    }

    [Fact]
    public async Task Ingest_missing_recipient_is_400()
    {
        var res = await host.Api.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "a@x.com", subject = "hi", text = "yo" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_malformed_json_is_400_not_500()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = new StringContent("{ not valid json ", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WebTestHost.ValidKey);
        var res = await host.Api.SendAsync(req);
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
    public async Task Ingest_multipart_with_attachments_preserves_them_in_the_spooled_message()
    {
        var csv = System.Text.Encoding.UTF8.GetBytes("id,name\n1,alice\n");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 }; // PNG magic + bytes

        var file1 = new ByteArrayContent(csv);
        file1.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var file2 = new ByteArrayContent(png);
        file2.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var content = new MultipartFormDataContent
        {
            { new StringContent("App <a@x.com>"), "from" },
            { new StringContent("b@y.com"), "to" },
            { new StringContent("With attachments"), "subject" },
            { new StringContent("see attached"), "text" },
        };
        content.Add(file1, "attachment", "report.csv");
        content.Add(file2, "attachment", "logo.png");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WebTestHost.ValidKey);
        var res = await host.Api.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        // The spooled .eml must carry both attachments, with names + bytes intact.
        var newest = new DirectoryInfo(host.Spool.IncomingDir).GetFiles("*.eml")
            .OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
        var msg = MimeMessage.Load(newest);
        var atts = msg.Attachments.OfType<MimePart>().ToDictionary(p => p.FileName!, p => p);

        Assert.Equal(2, atts.Count);
        Assert.Equal("see attached", msg.TextBody);
        AssertContent(atts["report.csv"], csv);
        AssertContent(atts["logo.png"], png);

        static void AssertContent(MimePart part, byte[] expected)
        {
            using var ms = new MemoryStream();
            part.Content!.DecodeTo(ms);
            Assert.Equal(expected, ms.ToArray());
        }
    }

    [Fact]
    public async Task Ingest_malformed_from_address_is_400_not_500()
    {
        // A bad address makes MailboxAddress.Parse throw; the handler must surface 400, never a 500.
        var res = await host.Api.SendAsync(Post(WebTestHost.ValidKey,
            new { from = "not an address <<<", to = new[] { "b@y.com" }, subject = "hi", text = "yo" }));
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
