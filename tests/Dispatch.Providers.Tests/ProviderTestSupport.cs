using System.Net;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

/// <summary>Shared helpers for the HTTP/JSON provider tests (Postmark, Resend, SparkPost, SMTP2GO).</summary>
internal static class ProviderTestSupport
{
    public static RelayMessage Message()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };
        return new RelayMessage { Message = mime, FromAddress = "sender@example.com", ToAddresses = ["rcpt@dest.com"] };
    }

    // A message with To + Cc headers and an extra envelope recipient (the true Bcc — present in the envelope
    // but in no visible header). Used to verify providers split To/Cc/Bcc and never expose the blind copy.
    public static RelayMessage MessageWithCcBcc()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("to@dest.com"));
        mime.Cc.Add(MailboxAddress.Parse("cc@dest.com"));
        mime.Subject = "Hi";
        mime.Body = new TextPart("plain") { Text = "body" };
        return new RelayMessage
        {
            Message = mime,
            FromAddress = "sender@example.com",
            ToAddresses = ["to@dest.com", "cc@dest.com", "bcc@hidden.com"],
        };
    }

    // A message carrying one attachment "hello.txt" with bytes "file-data" (base64: ZmlsZS1kYXRh).
    public const string AttachmentBase64 = "ZmlsZS1kYXRh";
    public static RelayMessage MessageWithAttachment()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@dest.com"));
        mime.Subject = "Hi";
        var builder = new BodyBuilder { TextBody = "body" };
        builder.Attachments.Add("hello.txt", "file-data"u8.ToArray(), ContentType.Parse("text/plain"));
        mime.Body = builder.ToMessageBody();
        return new RelayMessage { Message = mime, FromAddress = "sender@example.com", ToAddresses = ["rcpt@dest.com"] };
    }
}

/// <summary>Captures the outgoing request and returns a canned response, so providers can be exercised
/// without a live HTTP call (mirrors the StubHandler in MailgunProviderTests, shared across providers).</summary>
internal sealed class StubHttpHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public string? RequestUri { get; private set; }
    public string? AuthScheme { get; private set; }
    public string? AuthParameter { get; private set; }
    public string Body { get; private set; } = "";
    private HttpRequestMessage? _request;

    public string? Header(string name) =>
        _request is not null && _request.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _request = request;
        RequestUri = request.RequestUri?.ToString();
        AuthScheme = request.Headers.Authorization?.Scheme;
        AuthParameter = request.Headers.Authorization?.Parameter;
        if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}
