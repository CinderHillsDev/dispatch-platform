namespace Dispatch.Core.Licensing;

/// <summary>
/// Process-wide "is enforcement active?" flag (the licensing analogue of <c>IntakeState</c>). Written by
/// <see cref="LicenseWorker"/> from the evaluated <see cref="LicenseSnapshot"/>; read by the SMTP mailbox
/// filter, the HTTP ingestion handler, and the relay worker pool. When active, the product stops accepting
/// and relaying new mail; the spool and dashboard stay up so pasting a valid key recovers it. Register as a
/// singleton. Defaults to <b>not</b> enforced so a startup hiccup never wrongly blocks a licensed install.
/// </summary>
public sealed class LicenseGate
{
    private volatile bool _enforced;

    /// <summary>True once the install is unlicensed past the grace window - reject/pause new mail.</summary>
    public bool EnforcementActive => _enforced;

    public void Set(bool active) => _enforced = active;
}
