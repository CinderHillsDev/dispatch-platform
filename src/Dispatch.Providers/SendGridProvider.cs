using Dispatch.Core.Providers;
using MimeKit;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Dispatch.Providers;

/// <summary>
/// SendGrid upstream relay via the official Web API v3 SDK (spec §8.2). Maps the parsed message's
/// from/to/subject/bodies/attachments onto a SendGrid message. Settings: ApiKey. 429/5xx are transient.
/// </summary>
public sealed class SendGridProvider(RelayConfig config, ISendGridClientFactory clientFactory) : IRelayProvider
{
    public string Name => "SendGrid";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = config.Settings.TryGetValue("ApiKey", out var k) ? k : null;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("SendGrid relay 'ApiKey' is not configured.");

        var mime = message.Message;
        var fromMailbox = mime.From.Mailboxes.FirstOrDefault()
            ?? throw new InvalidOperationException("Message has no From address.");

        var msg = new SendGridMessage();
        msg.SetFrom(new EmailAddress(fromMailbox.Address, fromMailbox.Name));
        msg.SetSubject(mime.Subject ?? "");

        var recipients = message.ToAddresses.Count > 0
            ? message.ToAddresses
            : mime.To.Mailboxes.Select(m => m.Address).ToArray();
        foreach (var to in recipients)
            msg.AddTo(new EmailAddress(to));

        if (!string.IsNullOrEmpty(mime.TextBody)) msg.AddContent(MimeType.Text, mime.TextBody);
        if (!string.IsNullOrEmpty(mime.HtmlBody)) msg.AddContent(MimeType.Html, mime.HtmlBody);

        foreach (var part in mime.Attachments.OfType<MimePart>())
        {
            if (part.Content is null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            msg.AddAttachment(part.FileName ?? "attachment",
                Convert.ToBase64String(ms.ToArray()), part.ContentType?.MimeType);
        }

        var client = clientFactory.Create(apiKey);
        Response response;
        try
        {
            response = await client.SendEmailAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new TransientRelayException($"SendGrid request failed: {ex.Message}", ex);
        }

        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            var detail = $"SendGrid {status}: {await response.Body.ReadAsStringAsync(ct)}";
            if (status is 429 or >= 500) throw new TransientRelayException(detail);
            throw new InvalidOperationException(detail);
        }

        response.Headers.TryGetValues("X-Message-Id", out var ids);
        return RelayResult.Success(ids?.FirstOrDefault(), $"SendGrid {status}");
    }
}

/// <summary>Indirection so the SendGrid client can be faked in tests.</summary>
public interface ISendGridClientFactory
{
    ISendGridClient Create(string apiKey);
}

public sealed class SendGridClientFactory : ISendGridClientFactory
{
    public ISendGridClient Create(string apiKey) => new SendGridClient(apiKey);
}
