using System.Net;
using System.Text.Json;
using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

public class TelnyxProviderTests
{
    // Their send answers 202 Accepted with the message object under `data`. Accepted means queued: nothing
    // here says a recipient got it.
    private const string Accepted =
        """{"data":{"record_type":"email_message","id":"0c31f3c4-9a4e-4b8f-9b0e-4a0f3d2c1b0a","status":"accepted"},"suppressed":[]}""";

    private const string Uuid = "0c31f3c4-9a4e-4b8f-9b0e-4a0f3d2c1b0a";

    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Telnyx,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "telnyx_key" },
    };

    private static JsonElement Body(StubHttpHandler handler) => JsonDocument.Parse(handler.Body).RootElement;

    private static RelayMessage With(RelayMessage m, string? spoolId = null, string? subject = null)
    {
        var mime = m.Message;
        if (subject is not null) mime.Subject = subject;
        return new RelayMessage
        {
            Message = mime,
            FromAddress = m.FromAddress,
            ToAddresses = m.ToAddresses,
            SpoolId = spoolId ?? m.SpoolId,
        };
    }

    [Fact]
    public async Task Posts_to_email_messages_with_bearer_and_returns_the_message_uuid()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        var result = await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.telnyx.com/v2/email_messages", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("telnyx_key", handler.AuthParameter);

        var body = Body(handler);
        Assert.Equal("sender@example.com", body.GetProperty("from").GetString());
        Assert.Equal("rcpt@dest.com", body.GetProperty("to")[0].GetString());
        Assert.Equal("Hi", body.GetProperty("subject").GetString());
        Assert.Equal("body", body.GetProperty("text_body").GetString());
        Assert.False(body.TryGetProperty("html_body", out _), "no HTML part on this message, so none is sent");
        Assert.Equal(Uuid, result.ProviderMessageId);
    }

    [Fact]
    public async Task Two_zero_two_is_a_success_because_that_is_what_accepted_means_there()
    {
        // If the provider only recognised 200, every Telnyx send would look like a failure and burn retries,
        // which for a relay means duplicate mail the second time the mapping is fixed wrong.
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        var result = await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.StartsWith("HTTP 202", result.ProviderDetail);
        Assert.Equal(Uuid, result.ProviderMessageId);
    }

    [Fact]
    public async Task Keeps_the_from_display_name_as_an_address_object()
    {
        // The string form "Acme <a@b.com>" is ambiguous once a display name contains a comma or a bracket,
        // and mail rebuilt from an inbound message can carry exactly that.
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler)).SendAsync(
            Message("Acme, Inc. <sales>", "sender@example.com"), default);

        var from = Body(handler).GetProperty("from");
        Assert.Equal(JsonValueKind.Object, from.ValueKind);
        Assert.Equal("sender@example.com", from.GetProperty("email").GetString());
        Assert.Equal("Acme, Inc. <sales>", from.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Splits_to_cc_and_bcc()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithCcBcc(), default);

        var body = Body(handler);
        Assert.Equal("to@dest.com", body.GetProperty("to")[0].GetString());
        Assert.Equal("cc@dest.com", body.GetProperty("cc")[0].GetString());
        Assert.Equal("bcc@hidden.com", body.GetProperty("bcc")[0].GetString());
        Assert.DoesNotContain("bcc@hidden.com", body.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Sends_reply_to_as_a_bare_address_because_the_name_is_dropped_anyway()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler)).SendAsync(
            Message(null, "noreply@example.com", replyTo: ("Support", "support@example.com")), default);

        Assert.Equal("support@example.com", Body(handler).GetProperty("reply_to").GetString());
    }

    [Fact]
    public async Task Sends_both_bodies_when_the_message_carries_both()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.Subject = "Hi";
        mime.Body = new BodyBuilder { TextBody = "plain", HtmlBody = "<b>rich</b>" }.ToMessageBody();

        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler)).SendAsync(
            new RelayMessage { Message = mime, FromAddress = "sender@example.com", ToAddresses = ["rcpt@dest.com"] },
            default);

        var body = Body(handler);
        Assert.Equal("plain", body.GetProperty("text_body").GetString());
        Assert.Contains("<b>rich</b>", body.GetProperty("html_body").GetString());
    }

    [Fact]
    public async Task Forwards_attachments_as_base64()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithAttachment(), default);

        var att = Body(handler).GetProperty("attachments")[0];
        Assert.Equal("hello.txt", att.GetProperty("filename").GetString());
        Assert.Equal(ProviderTestSupport.AttachmentBase64, att.GetProperty("content").GetString());
        Assert.Equal("text/plain", att.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task Keys_the_send_by_spool_id_and_body_so_a_retry_cannot_double_deliver()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(With(ProviderTestSupport.Message(), "6f6a4c1e-9a3b-4e71-9d2c-8f0c3b5a7d11"), default);

        var key = handler.Header("Idempotency-Key");
        Assert.NotNull(key);
        Assert.StartsWith("6f6a4c1e-9a3b-4e71-9d2c-8f0c3b5a7d11-", key);
        Assert.Matches("^[A-Za-z0-9_-]+$", key);
        Assert.InRange(key!.Length, 1, 255);
    }

    [Fact]
    public async Task The_same_message_retried_produces_the_same_key()
    {
        // The case the header exists for: the request went through, the response was lost, the worker retried.
        var a = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        var b = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        var message = With(ProviderTestSupport.Message(), "spool-1");

        await new TelnyxProvider(Config(), new HttpClient(a)).SendAsync(message, default);
        await new TelnyxProvider(Config(), new HttpClient(b)).SendAsync(message, default);

        Assert.Equal(a.Header("Idempotency-Key"), b.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task A_changed_message_gets_a_fresh_key_so_a_fixed_send_really_sends()
    {
        // Their docs bind a replay to the same request but do not promise that a failed send releases the key.
        // Keying on content digest is what stops "edit the message and retry" from replaying a stored failure.
        var a = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        var b = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);

        await new TelnyxProvider(Config(), new HttpClient(a))
            .SendAsync(With(ProviderTestSupport.Message(), "spool-1", subject: "first attempt"), default);
        await new TelnyxProvider(Config(), new HttpClient(b))
            .SendAsync(With(ProviderTestSupport.Message(), "spool-1", subject: "corrected"), default);

        Assert.NotEqual(a.Header("Idempotency-Key"), b.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Sends_without_an_idempotency_key_when_there_is_no_spool_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Null(handler.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task A_spool_id_that_is_not_key_safe_is_sanitised_not_rejected()
    {
        // Their header accepts letters, digits, '-' and '_' only; anything else would be dropped or 400'd.
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(With(ProviderTestSupport.Message(), "spool/2026\\10:22 #7"), default);

        // "spool/2026\10:22 #7" -> the '/', '\', ':', ' ' and '#' are the characters their spec rejects.
        Assert.Matches(@"^spool202610227-[0-9A-F]{24}$", handler.Header("Idempotency-Key")!);
    }

    [Fact]
    public async Task A_very_long_spool_id_stays_inside_their_key_length_limit()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(With(ProviderTestSupport.Message(), new string('a', 400)), default);

        var key = handler.Header("Idempotency-Key")!;
        Assert.Equal(255, key.Length);                       // the id is truncated to leave the digest room
        Assert.Matches(@"^a{230}-[0-9A-F]{24}$", key);
    }

    [Fact]
    public async Task Names_suppressed_recipients_because_a_partial_send_is_not_a_clean_success()
    {
        var handler = new StubHttpHandler(HttpStatusCode.Accepted,
            $$"""{"data":{"record_type":"email_message","id":"{{Uuid}}","status":"accepted"},"suppressed":[{"to":"gone@dest.com","reason":"hard_bounce"}]}""");
        var result = await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal(Uuid, result.ProviderMessageId);
        Assert.Contains("gone@dest.com (hard_bounce)", result.ProviderDetail);
        Assert.Contains("NOT sent", result.ProviderDetail);
    }

    [Fact]
    public async Task Many_suppressed_recipients_are_counted_rather_than_listed()
    {
        var items = string.Join(",", Enumerable.Range(1, 7).Select(i => $$"""{"to":"r{{i}}@dest.com","reason":"suppression"}"""));
        var handler = new StubHttpHandler(HttpStatusCode.Accepted,
            $$"""{"data":{"record_type":"email_message","id":"{{Uuid}}","status":"accepted"},"suppressed":[{{items}}]}""");
        var result = await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Contains("7 recipient(s) suppressed", result.ProviderDetail);
        Assert.DoesNotContain("r1@dest.com", result.ProviderDetail);
    }

    [Fact]
    public async Task A_success_body_without_a_readable_id_is_still_a_success()
    {
        // A shape change on their side must not turn delivered mail into a retry storm.
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, """{"data":{"record_type":"email_message"}}""");
        var result = await new TelnyxProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Null(result.ProviderMessageId);
        Assert.Contains("HTTP 202", result.ProviderDetail);
    }

    [Fact]
    public async Task Rate_limited_and_server_side_failures_are_transient()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable })
        {
            var http = new HttpClient(new StubHttpHandler(status,
                """{"errors":[{"code":"10007","title":"Too Many Requests","detail":"slow down"}]}"""));
            await Assert.ThrowsAsync<TransientRelayException>(() =>
                new TelnyxProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
        }
    }

    [Fact]
    public async Task Validation_and_auth_errors_are_permanent_and_carry_their_detail()
    {
        // 422 is their validation envelope; retrying it identically can only burn the retry budget.
        var http = new HttpClient(new StubHttpHandler((HttpStatusCode)422,
            """{"errors":[{"code":"10015","title":"Validation Failed","detail":"from is not a verified sending address"}]}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TelnyxProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));

        Assert.Contains("verified sending address", ex.Message);
        Assert.Contains("422", ex.Message);
    }

    [Fact]
    public async Task All_recipients_suppressed_is_a_permanent_failure_not_a_success()
    {
        // They answer 4xx with the suppressed list when nothing was sent; it must not read as delivered.
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.UnprocessableEntity,
            """{"errors":[{"code":"20200","title":"All Recipients Suppressed","detail":"every recipient is suppressed"}],"suppressed":[{"to":"gone@dest.com","reason":"hard_bounce"}]}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TelnyxProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Missing_api_key_fails_before_any_http_call()
    {
        var config = new RelayConfig { Provider = RelayProviderType.Telnyx };
        var handler = new StubHttpHandler(HttpStatusCode.Accepted, Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TelnyxProvider(config, new HttpClient(handler)).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Null(handler.RequestUri);
    }

    private static RelayMessage Message(string? fromName, string fromAddress, (string, string)? replyTo = null)
    {
        var mime = new MimeMessage();
        mime.From.Add(fromName is null
            ? MailboxAddress.Parse(fromAddress)
            : new MailboxAddress(fromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        if (replyTo is { } r) mime.ReplyTo.Add(new MailboxAddress(r.Item1, r.Item2));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };
        return new RelayMessage { Message = mime, FromAddress = fromAddress, ToAddresses = ["rcpt@dest.com"] };
    }
}
