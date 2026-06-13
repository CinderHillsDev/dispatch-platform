namespace Dispatch.Core.Configuration;

/// <summary>SMTP listener settings (bound from the "Listener" config section).</summary>
public sealed class ListenerOptions
{
    public const string SectionName = "Listener";

    /// <summary>Ports to listen on. Empty falls back to <see cref="DefaultPorts"/> (2525; 25/587 need elevation).
    /// Left empty by default so configuration values replace rather than append (array-binding quirk).</summary>
    public int[] Ports { get; set; } = [];

    public string ServerName { get; set; } = "Dispatch";

    /// <summary>Application-layer allow-list. Empty falls back to <see cref="DefaultAllowedCidrs"/> (localhost only).</summary>
    public string[] AllowedCidrs { get; set; } = [];

    public static readonly int[] DefaultPorts = [2525];
    public static readonly string[] DefaultAllowedCidrs = ["127.0.0.1/32", "::1/128"];

    public int[] EffectivePorts => Ports is { Length: > 0 } ? Ports : DefaultPorts;
    public string[] EffectiveAllowedCidrs => AllowedCidrs is { Length: > 0 } ? AllowedCidrs : DefaultAllowedCidrs;

    /// <summary>Global max message size ceiling in bytes. 0 = no limit.</summary>
    public long MaxMessageBytes { get; set; } = 0;

    /// <summary>Require SMTP AUTH against the configured credential allow-list (spec §5.3).</summary>
    public bool RequireAuth { get; set; } = false;

    /// <summary>Path to a PFX certificate enabling STARTTLS (empty = plain text only).</summary>
    public string TlsCertPath { get; set; } = "";
    public string TlsCertPassword { get; set; } = "";
}
