using Dispatch.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Auth;

/// <summary>
/// Enforces mandatory web-UI auth on the dashboard port (spec §17). Protects <c>/api/*</c> (except the auth
/// endpoints) and the SignalR hubs under <c>/hub/*</c>; static SPA assets and <c>/health</c> stay open so the
/// login / first-run screen can load. The ingestion API (separate port) is unaffected — it uses API keys —
/// but the dashboard hubs are never exposed there: a <c>/hub/*</c> request on any non-web port is 404'd so the
/// live relay-event stream (which carries sender/recipient/subject metadata) can't be harvested anonymously.
/// </summary>
public sealed class WebAuthMiddleware(IOptions<WebUiOptions> webUi) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isHub = path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase);

        if (ctx.Connection.LocalPort != webUi.Value.Port)
        {
            // The dashboard hubs must not be reachable on the ingestion (or any other) port.
            if (isHub)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(ctx);
            return;
        }

        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);

        if ((isApi || isHub) && !(ctx.User.Identity?.IsAuthenticated ?? false))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(ctx);
    }
}
