using Dispatch.Web.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>Web-UI self-update endpoints (dashboard port): report status and accept an uploaded, signed
/// upgrade package. The actual apply + restart is done by the platform updater (see UpdateService).</summary>
public static class UpdateEndpoints
{
    public static void MapUpdates(this RouteGroupBuilder group)
    {
        group.MapGet("/updates/status", async (UpdateService svc, CancellationToken ct) =>
            Results.Ok(await svc.StatusAsync(ct)));

        // Dismiss the "you just upgraded" notice after the admin has seen it (shown once, post-upgrade login).
        group.MapPost("/updates/dismiss-notice", (UpdateService svc) =>
        {
            svc.DismissNotice();
            return Results.Ok(new { ok = true });
        });

        // Multipart: bundle (.tar.gz) + manifest (.json) + signature (.sig). Verified + staged, then handed
        // to the platform updater. (Reads the form manually, so no antiforgery token is required.)
        group.MapPost("/updates/upload", async (HttpRequest req, UpdateService svc, CancellationToken ct) =>
        {
            var r = await svc.HandleUploadAsync(req, req.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Json(new { ok = r.Ok, message = r.Message, version = r.Version }, statusCode: r.Status);
        });
    }
}
