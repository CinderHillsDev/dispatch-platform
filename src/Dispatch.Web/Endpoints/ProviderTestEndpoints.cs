using Dispatch.Web.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Provider-test endpoints (spec §11.4). The test runs the same code path a live message would take,
/// using the credentials supplied in the request — they do not need to be saved first. Log lines stream
/// live over the <c>/hub/test-provider</c> SignalR hub and can also be polled here.
/// </summary>
public static class ProviderTestEndpoints
{
    public static void MapProviderTest(this RouteGroupBuilder group)
    {
        group.MapPost("/config/test-provider", (TestProviderRequest req, ProviderTestService service) =>
        {
            if (string.IsNullOrWhiteSpace(req.Provider))
                return Results.BadRequest(new { error = "'provider' is required." });
            if (string.IsNullOrWhiteSpace(req.TestRecipient))
                return Results.BadRequest(new { error = "'testRecipient' is required." });

            try
            {
                var run = service.StartTest(req);
                return Results.Ok(new { runId = run.RunId, status = run.Status });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/config/test-provider/{runId}", (string runId, ProviderTestService service) =>
        {
            var run = service.Get(runId);
            if (run is null) return Results.NotFound();
            return Results.Ok(new
            {
                runId = run.RunId,
                status = run.Status,
                provider = run.Provider,
                durationMs = run.DurationMs,
                lines = run.Lines.Select(l => new { ts = l.Ts, level = l.Level, message = l.Message }),
            });
        });
    }
}
