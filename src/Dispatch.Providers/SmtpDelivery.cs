using Dispatch.Core.Providers;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>
/// Shared MailKit connect+send core for every SMTP-transport relay provider (generic SMTP, Google
/// Workspace direct, Microsoft 365 direct) - only the effective host/port/TLS/credentials differ per
/// provider; the connection, envelope handling, and error classification are identical.
/// </summary>
internal static class SmtpDelivery
{
    public static async Task<RelayResult> SendAsync(
        string host, int port, SecureSocketOptions secure, string? user, string? pass,
        RelayMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, secure, ct);
            if (!string.IsNullOrEmpty(user))
                await client.AuthenticateAsync(user, pass ?? "", ct);

            // Deliver to the SMTP envelope recipients (MAIL FROM / RCPT TO), not whatever the message headers
            // happen to list - otherwise MailKit derives recipients from To/Cc/Bcc headers and silently drops
            // Bcc recipients (which are envelope-only, never in the headers) and any header/envelope mismatch.
            var recipients = (message.ToAddresses.Count > 0
                    ? message.ToAddresses
                    : message.Message.To.Mailboxes.Select(m => m.Address))
                .Select(MailboxAddress.Parse).ToList();

            string response;
            if (recipients.Count > 0)
            {
                var sender = !string.IsNullOrWhiteSpace(message.FromAddress)
                    ? MailboxAddress.Parse(message.FromAddress)
                    : message.Message.From.Mailboxes.FirstOrDefault()
                        ?? throw new InvalidOperationException("Message has no sender address.");
                response = await client.SendAsync(message.Message, sender, recipients, ct);
            }
            else
            {
                response = await client.SendAsync(message.Message, ct);
            }
            await client.DisconnectAsync(quit: true, ct);

            // Spec §11.6 detail format (250 + server response line).
            return RelayResult.Success(detail: $"250 {response}");
        }
        catch (Exception ex) when (SmtpProvider.IsTransient(ex))
        {
            throw new TransientRelayException(ex.Message, ex);
        }
    }
}
