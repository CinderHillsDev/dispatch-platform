using Dispatch.Core.Providers;
using MailKit.Security;

namespace Dispatch.Providers;

/// <summary>
/// Direct, unauthenticated delivery to a Microsoft 365 tenant's inbound mail endpoint (spec §8.7) - the
/// same idea as <see cref="GoogleWorkspaceProvider"/>, but the endpoint is tenant-specific
/// (&lt;tenant&gt;.mail.protection.outlook.com), so it has to be typed in rather than assumed. Settings: Host.
/// Only use this for domains actually hosted on Microsoft 365; RCPT TO acceptance at the endpoint is what
/// enforces that, not this code.
/// </summary>
public sealed class Microsoft365Provider(RelayConfig config) : IRelayProvider
{
    private const int Port = 25;

    public string Name => "Microsoft365";

    public Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var host = Setting("Host");
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Microsoft 365 relay 'Host' is not configured.");

        return SmtpDelivery.SendAsync(host, Port, SecureSocketOptions.StartTls, user: null, pass: null, message, ct);
    }

    private string? Setting(string key) =>
        config.Settings.TryGetValue(key, out var v) ? v : null;
}
