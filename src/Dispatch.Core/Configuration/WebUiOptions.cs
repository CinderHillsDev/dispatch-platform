namespace Dispatch.Core.Configuration;

/// <summary>Web UI / read-API settings (spec §9, §12), bound from the "WebUi" config section.</summary>
public sealed class WebUiOptions
{
    public const string SectionName = "WebUi";

    public int Port { get; set; } = 8080;
}
