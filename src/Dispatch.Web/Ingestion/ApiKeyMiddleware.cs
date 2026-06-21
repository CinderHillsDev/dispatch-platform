using System.Net;
using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Dispatch.Core.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dispatch.Web.Ingestion;

/// <summary>
/// Authenticates HTTP ingestion requests (spec §7.3, §7.7): enforces the CIDR allow-list (403), a valid
/// <c>Bearer dsp_live_…</c> key (401), and a per-key rate limit (429). Only runs on the API port; other
/// ports pass straight through. The verified <see cref="ApiKey"/> is stashed in <c>HttpContext.Items</c>.
/// Every denial is recorded (spec §7.2): the Denied counter is incremented and, when logging is enabled,
/// a Denied row is written to relay_log so refusals are visible in the Message Log.
/// </summary>
public sealed class ApiKeyMiddleware(
    IApiKeyRepository keys,
    RateLimiter limiter,
    ApiKeyCache keyCache,
    ConfigCache config,
    ILogRepository logRepo,
    ILoggingSettings loggingSettings,
    Dispatch.Core.Counters.ICounterRepository counters,
    ILogger<ApiKeyMiddleware> log) : IMiddleware
{
    public const string ApiKeyItem = "ApiKey";

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        // Live API settings from the config cache (spec §12.5): allow-list/rate-limit edits apply at once.
        var o = config.Api();
        if (!o.IsApiPort(ctx.Connection.LocalPort))
        {
            await next(ctx);   // not an ingestion port (plain HTTP or HTTPS) — leave it to the dashboard pipeline
            return;
        }

        if (!IsAllowed(ctx.Connection.RemoteIpAddress, o))
        {
            // 403 (never 401) so we don't leak whether a valid key was present (§7.2).
            log.LogWarning("API request denied from {Ip} (not in allow-list)", ctx.Connection.RemoteIpAddress);
            await DenyAsync(ctx, null, "Source IP not in allow-list");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header) ||
            !header.ToString().StartsWith("Bearer dsp_live_", StringComparison.Ordinal))
        {
            await DenyAsync(ctx, null, "Missing or malformed API key");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var raw = header.ToString()["Bearer ".Length..];
        // 30s verified-key cache to avoid a bcrypt compare on every request (spec §7.7).
        var key = keyCache.Get(raw);
        if (key is null)
        {
            key = await keys.VerifyAsync(raw, ctx.RequestAborted);
            if (key is null)
            {
                await DenyAsync(ctx, null, "Invalid or revoked API key");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            keyCache.Set(raw, key);
        }

        var limit = key.RateLimitPerMinute > 0 ? key.RateLimitPerMinute : o.RateLimitPerKey;
        if (!limiter.TryAcquire(key.KeyId, limit))
        {
            await DenyAsync(ctx, key, "Rate limit exceeded");
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "60";
            return;
        }

        await keys.RecordUsageAsync(key.Id, ctx.RequestAborted);
        ctx.Items[ApiKeyItem] = key;
        await next(ctx);
    }

    /// <summary>Records a denial: always counts it, and (when enabled) writes a Denied relay_log row. Best-effort.</summary>
    private async Task DenyAsync(HttpContext ctx, ApiKey? key, string reason)
    {
        var ct = ctx.RequestAborted;
        try { await counters.IncrementAsync(0, Dispatch.Core.Counters.CounterField.Denied, ct); }
        catch (Exception ex) { log.LogError(ex, "Denied-counter increment failed"); }

        try
        {
            if (!await loggingSettings.LogDeniedAsync(ct)) return;
            await logRepo.InsertAsync(new RelayLogEntry
            {
                Event = "Denied",
                Status = "Denied",
                Error = reason,
                IngestSource = "API",
                SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
                ApiKeyId = key?.Id,
                ApiKeyName = key?.Name,
            }, ct);
        }
        catch (Exception ex) { log.LogError(ex, "Denied relay_log insert failed (rejection unaffected)"); }
    }

    private static bool IsAllowed(IPAddress? remote, ApiOptions o)
    {
        // Closed model (spec §17.10): only source IPs in the allow-list may call the API. An empty list
        // denies everyone; to allow all sources, add 0.0.0.0/0 + ::/0 explicitly in Access Control.
        var cidrs = o.EffectiveAllowedCidrs;
        if (cidrs.Length == 0) return false;
        if (remote is null) return false;

        var test = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        foreach (var c in cidrs)
        {
            if (IPNetwork.TryParse(c, out var net) && (net.Contains(test) || net.Contains(remote)))
                return true;
        }
        return false;
    }
}
