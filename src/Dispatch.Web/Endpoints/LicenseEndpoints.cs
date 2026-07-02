using Dispatch.Core.Audit;
using Dispatch.Core.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Licensing status + key entry for the dashboard (spec: commercial in-product licensing). Verification is
/// entirely offline. GET returns the effective state - including this install's node-lock <c>machineId</c>,
/// which the customer sends when purchasing - and POST validates and stores a pasted key, then refreshes the
/// enforcement gate so mail flow resumes immediately without waiting for the worker's next tick.
/// </summary>
public static class LicenseEndpoints
{
    public sealed record SetLicenseRequest(string? Key);

    public static void MapLicense(this RouteGroupBuilder group)
    {
        group.MapGet("/license", async (LicenseService license, CancellationToken ct) =>
            Results.Ok(await BuildStatusAsync(license, ct)));

        group.MapPost("/license", async (
            SetLicenseRequest req, LicenseService license, LicenseGate gate, IAuditLog audit,
            HttpContext http, CancellationToken ct) =>
        {
            var (ok, error, status) = await license.SaveKeyAsync(req.Key, ct);
            if (!ok)
            {
                await audit.Audit("License", "License key rejected (invalid for this machine)", "Warning", "admin",
                    http.Connection.RemoteIpAddress?.ToString());
                return Results.BadRequest(new { error });
            }

            // Re-evaluate and update the enforcement gate now, so intake/relay resume without the worker's delay.
            var snap = await license.EvaluateAsync(ct);
            gate.Set(snap.EnforcementActive);

            await audit.Audit("License", $"License key accepted ({status.LicenseId})", "Notice", "admin",
                http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(await BuildStatusAsync(license, ct));
        });
    }

    private static async Task<object> BuildStatusAsync(LicenseService license, CancellationToken ct)
    {
        var machineId = await license.GetMachineIdAsync(ct);
        var snap = await license.EvaluateAsync(ct);
        var s = snap.Status;

        var state =
            s.Licensed ? "licensed" :
            s.Revoked ? "revoked" :
            s.Expired ? "expired" :
            snap.InGracePeriod ? "grace" :
            snap.HasKey ? "invalid" :
            "unlicensed";

        return new
        {
            machineId,
            state,
            hasKey = snap.HasKey,
            licensed = s.Licensed,
            licenseId = s.LicenseId,
            perpetual = s.Perpetual,
            expiresAt = s.ExpiresAt,
            expired = s.Expired,
            revoked = s.Revoked,
            inGracePeriod = snap.InGracePeriod,
            graceDaysRemaining = snap.GraceDaysRemaining,
            graceEndsUtc = snap.GraceEndsUtc,
            enforcementActive = snap.EnforcementActive,
            error = s.Error,
        };
    }
}
