using Dispatch.Core.Configuration;

namespace Dispatch.Core.Licensing;

/// <summary>
/// The effective licensing state of this install: the verified key status plus the first-run grace window.
/// <see cref="Operational"/> is the single question the rest of the product asks - "may I accept/relay mail?".
/// </summary>
public sealed record LicenseSnapshot(
    LicenseStatus Status,
    bool HasKey,
    bool InGracePeriod,
    DateTime GraceEndsUtc,
    int GraceDaysRemaining)
{
    /// <summary>True while the product may operate: a valid license, or still inside the first-run grace.</summary>
    public bool Operational => Status.Licensed || InGracePeriod;

    /// <summary>True once the product must stop accepting/relaying new mail (unlicensed and past grace).</summary>
    public bool EnforcementActive => !Operational;
}

/// <summary>
/// Evaluates the install's license OFFLINE and gates enforcement. Reads the pasted key and grace anchor
/// straight from <see cref="IConfigRepository"/> (not the cached snapshot) so a key saved on the dashboard
/// takes effect immediately. Shared by the dashboard (status display) and the enforcement worker.
///
/// Enforcement model (per spec): a <b>30-day grace from first run</b> lets a new install run while a key is
/// obtained; after that, an unlicensed (or expired/revoked) install stops accepting and relaying new mail -
/// the spool and dashboard stay up so pasting a valid key recovers it. Grace is anchored at first run only;
/// it is not renewed when a key later expires.
/// </summary>
public sealed class LicenseService
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    private readonly IConfigRepository config;
    private readonly MachineIdentity machine;
    private readonly LicenseVerifier _verifier;

    public LicenseService(IConfigRepository config, MachineIdentity machine)
        : this(config, machine, LicenseVerifier.Default()) { }

    /// <summary>Test seam: inject a verifier built from a known key pair.</summary>
    internal LicenseService(IConfigRepository config, MachineIdentity machine, LicenseVerifier verifier)
    {
        this.config = config;
        this.machine = machine;
        _verifier = verifier;
    }

    /// <summary>Verify the stored key against this machine and fold in the grace window.</summary>
    public async Task<LicenseSnapshot> EvaluateAsync(CancellationToken ct = default)
    {
        var machineId = await machine.GetAsync(ct);
        var key = await config.GetAsync(ConfigKeys.LicenseKey, ct);
        var status = _verifier.Verify(key, machineId);   // null/blank key -> invalid, fail-closed

        var firstRun = await GetOrInitFirstRunAsync(ct);
        var graceEnds = firstRun + GracePeriod;
        var inGrace = !status.Licensed && DateTime.UtcNow < graceEnds;
        var remaining = (int)Math.Max(0, Math.Ceiling((graceEnds - DateTime.UtcNow).TotalDays));

        return new LicenseSnapshot(status, !string.IsNullOrWhiteSpace(key), inGrace, graceEnds, remaining);
    }

    /// <summary>This install's Machine ID (for display on the dashboard).</summary>
    public ValueTask<string> GetMachineIdAsync(CancellationToken ct = default) => machine.GetAsync(ct);

    /// <summary>
    /// Validate and store a pasted license key. Rejects a key whose signature is not authentic for this machine;
    /// an authentic-but-expired/revoked key is still stored (so the dashboard can explain the state).
    /// </summary>
    public async Task<(bool Ok, string? Error, LicenseStatus Status)> SaveKeyAsync(string? key, CancellationToken ct = default)
    {
        var machineId = await machine.GetAsync(ct);
        var status = _verifier.Verify(key, machineId);
        if (!status.SignatureValid)
            return (false, status.Error ?? "the license key is not valid for this machine", status);

        await config.SetAsync(ConfigKeys.LicenseKey, key!.Trim(), ct: ct);
        return (true, null, status);
    }

    private async Task<DateTime> GetOrInitFirstRunAsync(CancellationToken ct)
    {
        var raw = await config.GetAsync(ConfigKeys.LicenseFirstRunUtc, ct);
        if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
            return when.ToUniversalTime();

        var now = DateTime.UtcNow;
        await config.SetAsync(ConfigKeys.LicenseFirstRunUtc, now.ToString("O"), ct: ct);
        return now;
    }
}
