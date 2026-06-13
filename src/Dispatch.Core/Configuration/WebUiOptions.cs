namespace Dispatch.Core.Configuration;

/// <summary>Web UI / read-API settings (spec §9, §12), bound from the "WebUi" config section.</summary>
public sealed class WebUiOptions
{
    public const string SectionName = "WebUi";

    public int Port { get; set; } = 8080;

    /// <summary>
    /// When true the session cookie is marked <c>Secure</c> (sent only over HTTPS) and HSTS is enabled.
    /// Set this once the dashboard is fronted by TLS / a reverse proxy. Defaults to false so the cookie
    /// still works when the dashboard is reached over plain HTTP during local development.
    /// </summary>
    public bool RequireHttps { get; set; }
}
