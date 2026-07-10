using Dispatch.Core.Providers;

namespace Dispatch.Core.Relays;

/// <summary>
/// Guards against configuring a relay that delivers over <b>outbound</b> SMTP port 25 on a host that blocks it
/// (Azure). This concerns the upstream <i>delivery</i> port of the generic SMTP provider - NOT Dispatch's own
/// inbound SMTP listener (which also uses 25/587 but is unaffected, since inbound isn't blocked).
/// </summary>
public static class SmtpPortGuard
{
    /// <summary>
    /// True when this relay would make an outbound SMTP connection on port 25: the generic SMTP provider with
    /// an effective delivery port of 25. The SMTP provider defaults to port 25 when the port setting is blank,
    /// so a missing/blank port counts as 25.
    /// </summary>
    public static bool UsesOutboundPort25(RelayProviderType provider, IReadOnlyDictionary<string, string?> settings)
    {
        if (provider != RelayProviderType.Smtp) return false;
        var raw = settings.TryGetValue("Port", out var value) ? value : null;
        var port = int.TryParse(raw, out var p) ? p : 25;   // matches SmtpProvider: blank -> 25
        return port == 25;
    }

    /// <summary>Explanation shown when the guard blocks a port-25 SMTP relay on Azure.</summary>
    public const string AzureBlockedMessage =
        "This server is running in Azure, which blocks outbound connections on port 25. Configure the SMTP " +
        "relay to use port 587 (submission) or 2525, or deliver through a provider's API instead.";
}
