using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Adds standard hardening response headers to the dashboard (web port only). The CSP is tuned for the
/// embedded React SPA: scripts/styles are served from the same origin (Vite emits external assets, no inline
/// scripts), <c>style-src 'unsafe-inline'</c> is allowed because React applies element <c>style</c> attributes,
/// and <c>connect-src</c> permits the same-origin SignalR WebSocket/long-poll. HSTS is emitted only when the
/// dashboard requires HTTPS (<see cref="Core.Configuration.WebUiOptions.RequireHttps"/>).
/// </summary>
public static class SecurityHeaders
{
    private const string Csp =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static void UseSecurityHeaders(this WebApplication app, int webPort, bool requireHttps)
    {
        app.UseWhen(ctx => ctx.Connection.LocalPort == webPort, branch =>
            branch.Use(async (ctx, next) =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Content-Security-Policy"] = Csp;
                if (requireHttps)
                    h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                await next(ctx);
            }));
    }
}
