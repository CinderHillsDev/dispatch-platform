namespace Dispatch.Core.Configuration;

/// <summary>SMTP listener settings (bound from the "Listener" config section).</summary>
public sealed class ListenerOptions
{
    public const string SectionName = "Listener";

    /// <summary>Ports to listen on. Empty falls back to <see cref="DefaultPorts"/> (2525; 25/587 need elevation).
    /// Left empty by default so configuration values replace rather than append (array-binding quirk).</summary>
    public int[] Ports { get; set; } = [];

    public string ServerName { get; set; } = "Dispatch";

    /// <summary>Application-layer source-IP allow-list. Empty = allow all; the safe baseline comes from the
    /// seeded defaults (loopback + private ranges for the listener — see <see cref="ConfigDefaults"/>), not a
    /// code-level fallback, so an operator who clears the list in the dashboard is opting in to allow-all.</summary>
    public string[] AllowedCidrs { get; set; } = [];

    public static readonly int[] DefaultPorts = [2525];

    public int[] EffectivePorts => Ports is { Length: > 0 } ? Ports : DefaultPorts;
    public string[] EffectiveAllowedCidrs => AllowedCidrs;

    /// <summary>Global max message size ceiling in bytes. 0 = no limit.</summary>
    public long MaxMessageBytes { get; set; } = 0;

    /// <summary>Require SMTP AUTH against the configured credential allow-list (spec §5.3).</summary>
    public bool RequireAuth { get; set; } = false;

    /// <summary>Allow SMTP AUTH over a plaintext (non-TLS) connection. Secure default is <c>false</c> — AUTH is
    /// only offered after STARTTLS so credentials are never sent in the clear. Enable for internal/dev use.</summary>
    public bool AllowUnsecureAuth { get; set; } = false;

    /// <summary>Path to a PFX certificate enabling STARTTLS (empty = plain text only).</summary>
    public string TlsCertPath { get; set; } = "";
    public string TlsCertPassword { get; set; } = "";

    /// <summary>Per-command wait timeout in seconds (spec §5.3). 0 = library default.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 60;

    /// <summary>Max concurrent SMTP connections (spec §5.3). 0 = unlimited; over the cap MAIL FROM is refused 421.</summary>
    public int MaxConnections { get; set; } = 100;
}
