using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

public class MailgunProviderTests
{
    private static RelayMessage TestMessage()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };
        return new RelayMessage { Message = mime, FromAddress = "sender@example.com", ToAddresses = ["rcpt@dest.com"] };
    }

    private static RelayConfig Config(string region = "US") => new()
    {
        Provider = RelayProviderType.Mailgun,
        Settings = new Dictionary<string, string?>
        {
            ["ApiKey"] = "key-secret",
            ["Domain"] = "mg.example.com",
            ["Region"] = region,
        },
    };

    [Fact]
    public async Task Posts_mime_to_us_endpoint_with_basic_auth()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"<msg-123>\"}");
        var result = await new MailgunProvider(Config(), new HttpClient(handler)).SendAsync(TestMessage(), default);

        Assert.Equal("https://api.mailgun.net/v3/mg.example.com/messages.mime", handler.RequestUri);
        Assert.Equal("Basic", handler.AuthScheme);
        var creds = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(handler.AuthParameter!));
        Assert.Equal("api:key-secret", creds);

        Assert.Equal("multipart/form-data", handler.ContentType);
        Assert.Contains("rcpt@dest.com", handler.Body);          // the "to" form field
        Assert.Contains("sender@example.com", handler.Body);     // the embedded raw MIME message
        Assert.Equal("<msg-123>", result.ProviderMessageId);
    }

    [Fact]
    public async Task Uses_eu_endpoint_when_region_eu()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"x\"}");
        await new MailgunProvider(Config("EU"), new HttpClient(handler)).SendAsync(TestMessage(), default);
        Assert.StartsWith("https://api.eu.mailgun.net/", handler.RequestUri!);
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "boom"));
        await Assert.ThrowsAsync<TransientRelayException>(() => new MailgunProvider(Config(), http).SendAsync(TestMessage(), default));
    }

    [Fact]
    public async Task Auth_error_is_permanent()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MailgunProvider(Config(), http).SendAsync(TestMessage(), default));
    }

    [Fact]
    public async Task Missing_domain_throws_permanent()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"));
        var cfg = new RelayConfig { Provider = RelayProviderType.Mailgun, Settings = new Dictionary<string, string?> { ["ApiKey"] = "k" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MailgunProvider(cfg, http).SendAsync(TestMessage(), default));
    }

    [Theory]
    [InlineData("mg.example.com/../admin")]
    [InlineData("mg.example.com/evil")]
    [InlineData("mg.example.com?x=1")]
    [InlineData("../secrets")]
    [InlineData("not a domain")]
    public async Task Invalid_domain_is_rejected_before_request(string domain)
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"));
        var cfg = new RelayConfig
        {
            Provider = RelayProviderType.Mailgun,
            Settings = new Dictionary<string, string?> { ["ApiKey"] = "k", ["Domain"] = domain },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MailgunProvider(cfg, http).SendAsync(TestMessage(), default));
    }

    private sealed class StubHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthParameter { get; private set; }
        public string? ContentType { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthParameter = request.Headers.Authorization?.Parameter;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
