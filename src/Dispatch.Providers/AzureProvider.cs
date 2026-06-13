using Azure;
using Azure.Communication.Email;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>
/// Azure Communication Services email relay via the SDK (spec §8.3).
/// Settings: ConnectionString, SenderAddress. Azure 429/5xx (RequestFailedException) are transient.
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
