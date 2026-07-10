using System.Net;
using Dispatch.Core.Configuration;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Web.Auth;

/// <summary>
/// Enforces the dashboard's access controls on the web port (spec §17). In order: a <c>webui.allowed_cidrs</c>
/// source-IP allow-list (403 outside it), then mandatory auth on <c>/api/*</c> (except the auth endpoints) and
/// the SignalR hubs under <c>/hub/*</c>; static SPA assets and <c>/health</c> stay open so the login / first-run
/// screen can load. The ingestion API (separate port) is unaffected - it uses API keys - but the dashboard hubs
/// are never exposed there: a <c>/hub/*</c> request on any non-web port is 404'd so the live relay-event stream
/// (which carries sender/recipient/subject metadata) can't be harvested anonymously. All settings are read live
/// from the <see cref="ConfigCache"/> (§12.5), so allow-list edits take effect without a restart.
/// </summary>
public sealed class WebAuthMiddleware(ConfigCache config) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isHub = path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase);

        if (ctx.Connection.LocalPort != config.GetInt(ConfigKeys.WebUiPort, 8420))
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

        // Source-IP allow-list for the admin UI (spec §17.10). Empty list = allow all.
        if (!IsAllowed(ctx.Connection.RemoteIpAddress, config.GetStringArray(ConfigKeys.WebUiAllowedCidrs, [])))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);

        if ((isApi || isHub) && !(ctx.User.Identity?.IsAuthenticated ?? false))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // CSRF defence-in-depth (spec §17): cookie auth is primarily protected by SameSite=Strict, but for
        // state-changing dashboard calls we also require the SPA's custom header. A cross-site page can't set
        // a custom header without a CORS preflight, which this app never grants - so a forged cross-origin
        // POST/PUT/DELETE is rejected even if SameSite is relaxed/unsupported. The SPA always sends it (lib/api.ts).
        if (isApi && IsMutating(ctx.Request.Method) && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(ctx);
    }

    /// <summary>Header the SPA sends on every state-changing request; its presence is the CSRF check.</summary>
    public const string CsrfHeader = "X-Dispatch-Request";

    private static bool IsMutating(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    private static bool IsAllowed(IPAddress? remote, string[] cidrs)
    {
        if (remote is null || cidrs.Length == 0) return true;
        var test = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        foreach (var c in cidrs)
            if (IPNetwork.TryParse(c, out var net) && (net.Contains(test) || net.Contains(remote)))
                return true;
        return false;
    }
}
