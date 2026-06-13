using System.Net;
using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Ingestion;

/// <summary>
/// Authenticates HTTP ingestion requests (spec §7.3, §7.7): enforces the CIDR allow-list (403), a valid
/// <c>Bearer dsp_live_…</c> key (401), and a per-key rate limit (429). Only runs on the API port; other
/// ports pass straight through. The verified <see cref="ApiKey"/> is stashed in <c>HttpContext.Items</c>.
/// </summary>
public sealed class ApiKeyMiddleware(
    IApiKeyRepository keys,
    RateLimiter limiter,
    IOptions<ApiOptions> options,
    ILogger<ApiKeyMiddleware> log) : IMiddleware
{
    public const string ApiKeyItem = "ApiKey";

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var o = options.Value;
        if (ctx.Connection.LocalPort != o.Port)
        {
            await next(ctx);   // not the ingestion port — leave it to the dashboard pipeline
            return;
        }

        if (!IsAllowed(ctx.Connection.RemoteIpAddress, o))
        {
            // 403 (never 401) so we don't leak whether a valid key was present (§7.2).
            log.LogWarning("API request denied from {Ip} (not in allow-list)", ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header) ||
            !header.ToString().StartsWith("Bearer dsp_live_", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var raw = header.ToString()["Bearer ".Length..];
        var key = await keys.VerifyAsync(raw, ctx.RequestAborted);
        if (key is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var limit = key.RateLimitPerMinute > 0 ? key.RateLimitPerMinute : o.RateLimitPerKey;
        if (!limiter.TryAcquire(key.KeyId, limit))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "60";
            return;
        }

        await keys.RecordUsageAsync(key.Id, ctx.RequestAborted);
        ctx.Items[ApiKeyItem] = key;
        await next(ctx);
    }

    private static bool IsAllowed(IPAddress? remote, ApiOptions o)
    {
        if (remote is null) return true;
        var cidrs = o.EffectiveAllowedCidrs;
        if (cidrs.Length == 0) return true;

        var test = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        foreach (var c in cidrs)
        {
            if (IPNetwork.TryParse(c, out var net) && (net.Contains(test) || net.Contains(remote)))
                return true;
        }
        return false;
    }
}
