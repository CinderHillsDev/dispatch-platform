using System.Net;
using System.Text.Json;
using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

public class EmailitProviderTests
{
    // Their success body. A 200 here means accepted/queued, not delivered.
    private const string Sent = """{"object":"email","id":"em_1A2B3C","status":"accepted"}""";

    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Emailit,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "secret_test" },
    };

    private static JsonElement Body(StubHttpHandler handler) => JsonDocument.Parse(handler.Body).RootElement;

    [Fact]
    public async Task Posts_to_v2_emails_with_bearer_and_returns_the_email_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        var result = await new EmailitProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.emailit.com/v2/emails", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("secret_test", handler.AuthParameter);

        var body = Body(handler);
        Assert.Equal("sender@example.com", body.GetProperty("from").GetString());
        Assert.Equal("rcpt@dest.com", body.GetProperty("to")[0].GetString());
        Assert.Equal("Hi", body.GetProperty("subject").GetString());
        Assert.Equal("body", body.GetProperty("text").GetString());
        Assert.False(body.TryGetProperty("html", out _), "no HTML part on this message, so none is sent");
        Assert.Equal("em_1A2B3C", result.ProviderMessageId);
    }

    [Fact]
    public async Task Keeps_the_from_display_name()
    {
        // A bare address would rewrite what the recipient sees; their from takes "Acme <a@b.com>".
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Acme", "sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };

        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler)).SendAsync(
            new RelayMessage { Message = mime, FromAddress = "sender@example.com", ToAddresses = ["rcpt@dest.com"] },
            default);

        Assert.Equal("Acme <sender@example.com>", Body(handler).GetProperty("from").GetString());
    }

    [Fact]
    public async Task Splits_to_cc_and_bcc()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithCcBcc(), default);

        var body = Body(handler);
        Assert.Equal("to@dest.com", body.GetProperty("to")[0].GetString());
        Assert.Equal("cc@dest.com", body.GetProperty("cc")[0].GetString());
        Assert.Equal("bcc@hidden.com", body.GetProperty("bcc")[0].GetString());
    }

    [Fact]
    public async Task Forwards_reply_to_so_replies_reach_the_author()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("noreply@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.ReplyTo.Add(new MailboxAddress("Support", "support@example.com"));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };

        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler)).SendAsync(
            new RelayMessage { Message = mime, FromAddress = "noreply@example.com", ToAddresses = ["rcpt@dest.com"] },
            default);

        Assert.Equal("Support <support@example.com>", Body(handler).GetProperty("reply_to")[0].GetString());
    }

    [Fact]
    public async Task Omits_reply_to_when_the_message_has_none()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.False(Body(handler).TryGetProperty("reply_to", out _));
    }

    [Fact]
    public async Task Forwards_attachments_as_base64()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithAttachment(), default);

        var att = Body(handler).GetProperty("attachments")[0];
        Assert.Equal("hello.txt", att.GetProperty("filename").GetString());
        Assert.Equal(ProviderTestSupport.AttachmentBase64, att.GetProperty("content").GetString());
        Assert.Equal("text/plain", att.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task Keys_the_send_by_spool_id_so_a_retry_cannot_double_deliver()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        var message = ProviderTestSupport.Message();
        await new EmailitProvider(Config(), new HttpClient(handler)).SendAsync(
            new RelayMessage
            {
                Message = message.Message,
                FromAddress = message.FromAddress,
                ToAddresses = message.ToAddresses,
                SpoolId = "6f6a4c1e-9a3b-4e71-9d2c-8f0c3b5a7d11",
            },
            default);

        Assert.Equal("6f6a4c1e-9a3b-4e71-9d2c-8f0c3b5a7d11", handler.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Sends_without_an_idempotency_key_when_there_is_no_spool_id()
    {
        // A made-up key per attempt would be worse than none: it dedupes nothing but still costs a header.
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await new EmailitProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Null(handler.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Rate_limited_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.TooManyRequests,
            """{"error":"Rate limit exceeded","retry_after":1}"""));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new EmailitProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Server_side_failures_are_transient()
    {
        // Their docs say the same for 500 and 503: retry with backoff.
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.ServiceUnavailable, "idempotency store down"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new EmailitProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Validation_and_auth_errors_are_permanent()
    {
        // 400 (bad address, disallowed attachment extension) and 401 must not burn the retry budget.
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized })
        {
            var http = new HttpClient(new StubHttpHandler(status, """{"error":"Validation failed"}"""));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EmailitProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
        }
    }

    [Fact]
    public async Task Missing_api_key_fails_before_any_http_call()
    {
        var config = new RelayConfig { Provider = RelayProviderType.Emailit };
        var handler = new StubHttpHandler(HttpStatusCode.OK, Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EmailitProvider(config, new HttpClient(handler)).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Null(handler.RequestUri);
    }
}
