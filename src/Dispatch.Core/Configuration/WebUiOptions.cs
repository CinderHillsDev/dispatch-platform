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
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Path to a PFX certificate for the dashboard HTTPS listener. This is the one exception to the
    /// "everything in SQL" rule (spec §12.1/§12.2): ASP.NET Core needs it to start the HTTPS listener
    /// before SQL is reachable, so it (and its password) live in appsettings.json, not the config table.
    /// </summary>
    public string TlsCertPath { get; set; } = "";
    public string TlsCertPassword { get; set; } = "";
}
