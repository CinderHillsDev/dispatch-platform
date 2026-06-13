namespace Dispatch.Core.Maintenance;

/// <summary>Graduated intake back-pressure level driven by spool free disk space (spec §14.1).</summary>
public enum IntakeLevel
{
    /// <summary>Plenty of free disk — accept normally.</summary>
    Normal = 0,

    /// <summary>Disk low — accept but delay each MAIL FROM by <see cref="IntakeState.ThrottleDelay"/> to slow the inbound rate.</summary>
    Throttled = 1,

    /// <summary>Disk critically low — reject inbound so senders queue and retry (RFC 5321 4xx).</summary>
    Suspended = 2,
}

/// <summary>
/// Process-wide intake back-pressure flag (spec §14.1). A single mutable level shared between the
/// disk monitor (writer) and the SMTP mailbox filter / dashboard (readers). The level is set from
/// free disk space on the spool partition by <see cref="DiskMonitor"/> and <see cref="PurgeWorker"/>.
/// Register as a singleton.
/// </summary>
public sealed class IntakeState
{
    // Thresholds in bytes. Below Warn we log; below Throttle we delay; below Suspend we reject.
    // Recovery uses the Throttle threshold so we return to Normal only once comfortably clear.
    public const long WarnBytes = 1024L * 1024 * 1024;       // 1 GB
    public const long ThrottleBytes = 500L * 1024 * 1024;    // 500 MB
    public const long SuspendBytes = 200L * 1024 * 1024;     // 200 MB

    /// <summary>How long <see cref="IntakeLevel.Throttled"/> delays each accepted MAIL FROM.</summary>
    public static readonly TimeSpan ThrottleDelay = TimeSpan.FromSeconds(2);

    private volatile int _level = (int)IntakeLevel.Normal;

    /// <summary>The current back-pressure level. Thread-safe to read from any thread.</summary>
    public IntakeLevel Level => (IntakeLevel)_level;

    /// <summary>
    /// Maps free disk space (bytes) to a level and applies it, returning the new level. When already
    /// throttled/suspended, recovery to a lower level requires crossing back above the throttle
    /// threshold so we don't flap right at a boundary.
    /// </summary>
    public IntakeLevel Apply(long freeBytes)
    {
        IntakeLevel next;
        if (freeBytes < SuspendBytes) next = IntakeLevel.Suspended;
        else if (freeBytes < ThrottleBytes) next = IntakeLevel.Throttled;
        else next = IntakeLevel.Normal;

        Interlocked.Exchange(ref _level, (int)next);
        return next;
    }
}
