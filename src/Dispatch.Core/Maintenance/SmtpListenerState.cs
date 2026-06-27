namespace Dispatch.Core.Maintenance;

/// <summary>
/// Process-wide record of the SMTP ports the listener actually bound (spec §5, §14.4). The configured
/// ports (<c>listener.ports</c>) and the bound ports can differ: the listener probes each port at startup
/// and falls back to 2525 when 25 can't be bound (in use or no privilege). The SMTP listener (writer)
/// publishes the resolved set here; the dashboard / <c>/health</c> (readers) surface it so operators see
/// what's truly listening, not just what was requested. Register as a singleton.
/// </summary>
public sealed class SmtpListenerState
{
    private volatile int[] _listeningPorts = [];

    /// <summary>The ports the listener is actually bound to (empty until it starts, or if none could bind).
    /// Reference reads/writes are atomic; safe to read from any thread.</summary>
    public int[] ListeningPorts
    {
        get => _listeningPorts;
        set => _listeningPorts = value ?? [];
    }
}
