using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>Operational endpoints used by upgrades/monitoring (spec §14, §16).</summary>
public static class ServiceEndpoints
{
    public static void MapServiceOps(this RouteGroupBuilder group)
    {
        // Waits for the in-flight spool (incoming + processing) to clear, for a graceful pre-upgrade stop.
        group.MapPost("/service/drain", async (SpoolDirectory spool, HttpContext ctx) =>
        {
            int Remaining() =>
                Directory.EnumerateFiles(spool.IncomingDir, "*.eml").Count() +
                Directory.EnumerateFiles(spool.ProcessingDir, "*.eml").Count();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Remaining() > 0 && DateTime.UtcNow < deadline && !ctx.RequestAborted.IsCancellationRequested)
                await Task.Delay(500, ctx.RequestAborted);

            var remaining = Remaining();
            return Results.Ok(new { drained = remaining == 0, remaining });
        });
    }
}
