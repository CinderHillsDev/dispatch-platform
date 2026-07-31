using Dispatch.Core.Providers;
using MailKit.Security;

namespace Dispatch.Providers;

/// <summary>
/// Direct, unauthenticated delivery to Google Workspace's unified inbound mail endpoint (spec §8.6). Google
/// consolidated inbound MX onto a single hostname for every Workspace domain, so unlike Microsoft 365 there
/// is nothing tenant-specific to configure - just point a routing rule at this relay for the recipient
/// domain instead of paying for a smart-host relay. Only use this for domains actually hosted on Google
/// Workspace; RCPT TO acceptance at the endpoint is what enforces that, not this code.
/// </summary>
public sealed class GoogleWorkspaceProvider : IRelayProvider
{
    private const string Host = "smtp.google.com";
    private const int Port = 25;

    public string Name => "GoogleWorkspace";

    public Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct) =>
        SmtpDelivery.SendAsync(Host, Port, SecureSocketOptions.StartTls, user: null, pass: null, message, ct);
}
