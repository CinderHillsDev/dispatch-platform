namespace Dispatch.Core.Configuration;

/// <summary>HTTP ingestion API settings (spec §7.2), bound from the "Api" config section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public int Port { get; set; } = 8081;

    /// <summary>Source-IP allow-list. Empty = allow all; the ingestion API is gated by API keys, so the
    /// seeded default is allow-all (see <see cref="ConfigDefaults"/>). Operators tighten it in the dashboard.</summary>
    public string[] AllowedCidrs { get; set; } = [];
    public long MaxMessageBytes { get; set; } = 0;
    public int RateLimitPerKey { get; set; } = 100;

    public string[] EffectiveAllowedCidrs => AllowedCidrs;
}
