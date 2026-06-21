namespace Dispatch.Core.Configuration;

/// <summary>HTTP ingestion API settings (spec §7.2), bound from the "Api" config section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public int Port { get; set; } = 8025;

    /// <summary>Whether the plain-HTTP listener is bound. Can be turned off to run HTTPS-only.</summary>
    public bool HttpEnabled { get; set; } = true;

    /// <summary>Whether an additional HTTPS listener (on <see cref="TlsPort"/>) is bound, using the shared
    /// TLS certificate (<see cref="TlsCertPath"/>).</summary>
    public bool TlsEnabled { get; set; }
    public int TlsPort { get; set; } = 8026;

    /// <summary>Shared TLS cert (PFX) path + password — also used by the SMTP listener's STARTTLS.</summary>
    public string TlsCertPath { get; set; } = "";
    public string TlsCertPassword { get; set; } = "";

    /// <summary>Source-IP allow-list (closed model — see <see cref="ConfigDefaults"/>): an empty list denies
    /// all. The ingestion API is additionally gated by API keys. Operators manage ranges in Access Control.</summary>
    public string[] AllowedCidrs { get; set; } = [];
    public long MaxMessageBytes { get; set; } = 0;
    public int RateLimitPerKey { get; set; } = 100;

    public string[] EffectiveAllowedCidrs => AllowedCidrs;

    /// <summary>True if <paramref name="localPort"/> is one of the API's bound ports (plain HTTP and/or
    /// HTTPS), respecting which listeners are enabled. Gates the ingestion endpoints + auth middleware.</summary>
    public bool IsApiPort(int localPort) =>
        (HttpEnabled && localPort == Port) || (TlsEnabled && localPort == TlsPort);
}
