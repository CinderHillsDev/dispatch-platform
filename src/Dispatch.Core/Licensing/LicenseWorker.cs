using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.Core.Licensing;

/// <summary>
/// Background licensing evaluator (spec: grace-then-stop). On a slow timer it re-evaluates the install's
/// license via <see cref="LicenseService"/> and flips <see cref="LicenseGate"/>, logging each transition.
/// License state is day-granular (grace/expiry), so a coarse cadence is plenty; the dashboard calls
/// <see cref="RefreshAsync"/> directly after a key is saved so mail resumes immediately.
///
/// Registered as a singleton AND a hosted service (like PurgeWorker) so the dashboard can trigger a refresh.
/// </summary>
public sealed class LicenseWorker(LicenseService license, LicenseGate gate, ILogger<LicenseWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private bool _lastEnforced;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);   // act immediately on startup
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RefreshAsync(stoppingToken);
        }
    }

    /// <summary>Re-evaluate the license and update the gate. Best-effort: never throws to the caller.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var snap = await license.EvaluateAsync(ct);
            gate.Set(snap.EnforcementActive);

            if (snap.EnforcementActive != _lastEnforced)
            {
                if (snap.EnforcementActive)
                    log.LogError("License enforcement ACTIVE - unlicensed past the {Days}-day grace. New mail is " +
                        "refused/paused until a valid license key is entered on the dashboard.",
                        (int)LicenseService.GracePeriod.TotalDays);
                else if (snap.Status.Licensed)
                    log.LogInformation("License valid ({LicenseId}). Enforcement inactive.", snap.Status.LicenseId);
                else
                    log.LogWarning("Unlicensed, but within the grace window ({Days} day(s) left). Enter a license key.",
                        snap.GraceDaysRemaining);
                _lastEnforced = snap.EnforcementActive;
            }
        }
        catch (Exception ex)
        {
            // Fail-open on evaluation error: a transient config/DB hiccup must not wrongly block a licensed install.
            log.LogError(ex, "License evaluation failed; leaving enforcement gate unchanged.");
        }
    }
}
