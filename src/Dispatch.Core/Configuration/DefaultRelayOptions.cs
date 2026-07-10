using Dispatch.Core.Providers;

namespace Dispatch.Core.Configuration;

/// <summary>The single default relay used until SQL-backed relays/routing land (bound from "DefaultRelay").</summary>
public sealed class DefaultRelayOptions
{
    public const string SectionName = "DefaultRelay";

    public string Name { get; set; } = "default";
    public RelayProviderType Provider { get; set; } = RelayProviderType.Unconfigured;
    public int MaxConcurrency { get; set; } = 4;
    public long MaxMessageBytes { get; set; } = 0;

    /// <summary>Provider-specific settings, e.g. SMTP Host/Port/Username/Password/TlsMode.</summary>
    public Dictionary<string, string?> Settings { get; set; } = new();
}
