using Azure;
using Azure.Communication.Email;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>
/// Azure Communication Services email relay via the SDK (spec §8.3).
/// Settings: ConnectionString, SenderAddress, optional AllowedSenders. ACS only accepts mail from a
/// MailFrom address configured on the verified domain, so when AllowedSenders is set the message's
/// From is validated against it and rejected locally (no ACS call) if it doesn't match. Azure 429/5xx
/// (RequestFailedException) are transient.
/// </summary>
public sealed class AzureProvider(RelayConfig config, IEmailClientFactory clientFactory) : IRelayProvider
{
    public string Name => "AzureCommunication";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var connectionString = config.Settings.TryGetValue("ConnectionString", out var cs) ? cs : null;
        var sender = config.Settings.TryGetValue("SenderAddress", out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Azure relay 'ConnectionString' is not configured.");
        if (string.IsNullOrWhiteSpace(sender))
            throw new InvalidOperationException("Azure relay 'SenderAddress' is not configured.");

        var mime = message.Message;

        // Closed policy: when AllowedSenders is defined, reject before touching ACS if the message's
        // From isn't one of the configured MailFrom addresses. A matching From is then used as the
        // ACS sender (it's a verified MailFrom); without AllowedSenders we keep the legacy behaviour
        // of always sending as SenderAddress.
        var allowed = ParseAllowedSenders(config.Settings.TryGetValue("AllowedSenders", out var a) ? a : null);
        if (allowed.Count > 0)
        {
            var from = !string.IsNullOrWhiteSpace(message.FromAddress)
                ? message.FromAddress
                : mime.From.Mailboxes.FirstOrDefault()?.Address;
            if (string.IsNullOrWhiteSpace(from) || !IsAllowedSender(from, allowed))
                throw new InvalidOperationException(
                    $"Sender '{from}' is not an allowed MailFrom for this Azure relay — message rejected and not sent to ACS. Allowed: {string.Join(", ", allowed)}.");
            sender = from;
        }

        var recipients = message.ToAddresses.Count > 0
            ? message.ToAddresses
            : mime.To.Mailboxes.Select(m => m.Address).ToArray();

        var content = new EmailContent(mime.Subject ?? "")
        {
            PlainText = mime.TextBody,
            Html = mime.HtmlBody,
        };
        var emailRecipients = new EmailRecipients(recipients.Select(r => new EmailAddress(r)).ToList());
        var emailMessage = new EmailMessage(sender, emailRecipients, content);

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

    /// <summary>Parses a comma/semicolon/newline-separated AllowedSenders list into trimmed entries.</summary>
    private static List<string> ParseAllowedSenders(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToList();

    /// <summary>
    /// True when <paramref name="from"/> matches an allowed entry. An entry containing a local part
    /// (e.g. "noreply@example.com") matches the full address; a bare domain ("example.com" or
    /// "@example.com") matches any address on that domain. Comparison is case-insensitive.
    /// </summary>
    private static bool IsAllowedSender(string from, IEnumerable<string> allowed)
    {
        var domain = from.Contains('@') ? from[(from.LastIndexOf('@') + 1)..] : "";
        foreach (var entry in allowed)
        {
            var e = entry.TrimStart('@');
            if (e.Contains('@'))
            {
                if (string.Equals(e, from, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (domain.Length > 0 && string.Equals(e, domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
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
