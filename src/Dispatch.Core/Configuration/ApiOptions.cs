namespace Dispatch.Core.Configuration;

/// <summary>HTTP ingestion API settings (spec §7.2), bound from the "Api" config section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public int Port { get; set; } = 8081;
    public string[] AllowedCidrs { get; set; } = [];
    public long MaxMessageBytes { get; set; } = 0;
    public int RateLimitPerKey { get; set; } = 100;

    public static readonly string[] DefaultAllowedCidrs = ["127.0.0.1/32", "::1/128"];
    public string[] EffectiveAllowedCidrs => AllowedCidrs is { Length: > 0 } ? AllowedCidrs : DefaultAllowedCidrs;
}
