using Azure;
using Azure.Communication.Email;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>
/// Azure Communication Services email relay via the SDK (spec §8.3).
/// Settings: ConnectionString, MailFrom (one or more comma-separated MailFrom addresses). ACS only accepts
/// mail from a MailFrom address explicitly configured on the resource (there is no domain-level wildcard),
/// so the message's From must exactly match one of the configured addresses or it's rejected locally (no
/// ACS call); a message with no From is sent as the first configured MailFrom. Azure 429/5xx
/// (RequestFailedException) are transient.
/// </summary>
public sealed class AzureProvider(RelayConfig config, IEmailClientFactory clientFactory) : IRelayProvider
{
    public string Name => "AzureCommunication";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var connectionString = config.Settings.TryGetValue("ConnectionString", out var cs) ? cs : null;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Azure relay 'ConnectionString' is not configured.");

        var mailFroms = ParseMailFroms(config.Settings.TryGetValue("MailFrom", out var mf) ? mf : null);
        if (mailFroms.Count == 0)
            throw new InvalidOperationException("Azure relay 'MailFrom' is not configured.");

        var mime = message.Message;

        // Closed policy: the message's From must be one of the configured MailFrom addresses (exact match —
        // ACS has no domain wildcard) or it's rejected before touching ACS. A message with no From is sent
        // as the first configured MailFrom. The matching/chosen MailFrom is the ACS sender.
        var from = !string.IsNullOrWhiteSpace(message.FromAddress)
            ? message.FromAddress
            : mime.From.Mailboxes.FirstOrDefault()?.Address;

        string sender;
        if (string.IsNullOrWhiteSpace(from))
            sender = mailFroms[0];
        else if (IsAllowedSender(from, mailFroms))
            sender = from;
        else
            throw new InvalidOperationException(
                $"Sender '{from}' is not a configured MailFrom for this Azure relay — message rejected and not sent to ACS. Configured: {string.Join(", ", mailFroms)}.");

        var rcpt = ProviderHttp.SplitRecipients(message);

        var content = new EmailContent(mime.Subject ?? "")
        {
            PlainText = mime.TextBody,
            Html = mime.HtmlBody,
        };
        var emailRecipients = new EmailRecipients(
            rcpt.To.Select(r => new EmailAddress(r)).ToList(),
            rcpt.Cc.Select(r => new EmailAddress(r)).ToList(),
            rcpt.Bcc.Select(r => new EmailAddress(r)).ToList());
        var emailMessage = new EmailMessage(sender, emailRecipients, content);

        foreach (var att in mime.Attachments)
        {
            if (att is not MimePart part || part.Content is null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            emailMessage.Attachments.Add(new EmailAttachment(
                part.FileName ?? "attachment",
                part.ContentType?.MimeType ?? "application/octet-stream",
                BinaryData.FromBytes(ms.ToArray())));
        }

        var client = clientFactory.Create(connectionString);
        try
        {
            var operation = await client.SendAsync(WaitUntil.Completed, emailMessage, ct);
            // Spec §11.6 detail format.
            return RelayResult.Success(operation.Id, $"OperationId: {operation.Id}, Status: {operation.Value.Status}");
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or >= 500)
        {
            throw new TransientRelayException($"Azure {ex.Status}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses a comma/semicolon/newline-separated MailFrom list into trimmed addresses.</summary>
    private static List<string> ParseMailFroms(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToList();

    /// <summary>
    /// True when <paramref name="from"/> exactly matches one of the configured MailFrom addresses
    /// (case-insensitive). ACS has no domain-level wildcard — each sender must be listed explicitly.
    /// </summary>
    private static bool IsAllowedSender(string from, IEnumerable<string> allowed) =>
        allowed.Any(e => string.Equals(e, from, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Indirection so the Azure EmailClient can be faked in tests.</summary>
public interface IEmailClientFactory
{
    EmailClient Create(string connectionString);
}

public sealed class EmailClientFactory : IEmailClientFactory
{
    public EmailClient Create(string connectionString) => new(connectionString);
}
