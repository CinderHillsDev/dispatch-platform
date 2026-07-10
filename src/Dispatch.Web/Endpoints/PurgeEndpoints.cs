using Dispatch.Core.Configuration;
using Dispatch.Core.Maintenance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Manual purge controls (spec §9.2 Storage &amp; Retention): an immediate out-of-schedule purge and the
/// recent-run history. Mapped on the dashboard port only and protected by the web-auth middleware. Mapped
/// directly by the host (not the shared dashboard group) because it depends on the purge worker.
/// </summary>
public static class PurgeEndpoints
{
    public static void MapPurgeOps(this IEndpointRouteBuilder app, int webPort)
    {
        app.MapPost("/api/purge/run", async (PurgeWorker worker, IPurgeSettings settings, CancellationToken ct) =>
        {
            var o = await settings.GetAsync(ct);
            var result = await worker.RunOnceAsync(o, ct, manual: true);
            return Results.Ok(result);
        }).AddEndpointFilter(WebPortOnly(webPort));

        app.MapGet("/api/purge/history", (PurgeHistory history) => Results.Ok(history.Snapshot()))
            .AddEndpointFilter(WebPortOnly(webPort));
    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> WebPortOnly(int webPort) =>
        async (ctx, next) => ctx.HttpContext.Connection.LocalPort == webPort ? await next(ctx) : Results.NotFound();
}
