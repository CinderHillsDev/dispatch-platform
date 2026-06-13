using Dispatch.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Auth;

/// <summary>
/// Enforces mandatory web-UI auth on the dashboard port (spec §17). Protects <c>/api/*</c> except the auth
/// endpoints; static SPA assets and <c>/health</c> stay open so the login / first-run screen can load. The
/// ingestion API (separate port) is unaffected — it uses API keys.
/// </summary>
public sealed class WebAuthMiddleware(IOptions<WebUiOptions> webUi) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Connection.LocalPort != webUi.Value.Port)
        {
            await next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "";
        var isProtected = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);

        if (isProtected && !(ctx.User.Identity?.IsAuthenticated ?? false))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(ctx);
    }
}
