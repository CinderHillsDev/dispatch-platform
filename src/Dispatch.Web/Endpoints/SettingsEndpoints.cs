using System.Globalization;
using System.Text.Json;
using Dispatch.Core.Audit;
using Dispatch.Core.Configuration;
using Dispatch.Data.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Live settings stored in the SQL config table (spec §12). Covers the relay_log suppression toggles,
/// the retry/back-off policy (spool.max_retries / spool.retry_delays_seconds), and the purge retention
/// thresholds (purge.*). Each value falls back to the appsettings default when unset. Changes take effect
/// within the relevant settings cache TTL — no restart. Listener ports and spool worker count are NOT
/// editable here: they require a restart and are surfaced read-only in the UI.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder group)
    {
        // Effective listener / HTTP-API / web-UI configuration from the SQL config cache (spec §9.3 GET
        // /api/config, §12.5). Allow-lists/sizes/rate-limit apply live; ports/spool/TLS apply on restart.
        group.MapGet("/config", (ConfigCache cache) =>
        {
            var l = cache.Listener();
            var a = cache.Api();
            var w = cache.WebUi();
            var sp = cache.Spool();
            return Results.Ok(new
            {
                listener = new
                {
                    ports = l.EffectivePorts,
                    serverName = l.ServerName,
                    allowedCidrs = l.EffectiveAllowedCidrs,
                    maxMessageBytes = l.MaxMessageBytes,
                    requireAuth = l.RequireAuth,
                    tlsEnabled = !string.IsNullOrWhiteSpace(l.TlsCertPath),
                    appliesOnRestart = new[] { "ports", "serverName", "requireAuth", "tls" },
                },
                // Shared TLS certificate (SMTP STARTTLS + HTTPS API).
                tls = new { source = cache.GetString(ConfigKeys.TlsCertSource, "") },
                api = new
                {
                    port = a.Port,
                    httpEnabled = a.HttpEnabled,
                    tlsEnabled = a.TlsEnabled,
                    tlsPort = a.TlsPort,
                    allowedCidrs = a.EffectiveAllowedCidrs,
                    maxMessageBytes = a.MaxMessageBytes,
                    rateLimitPerKey = a.RateLimitPerKey,
                    appliesOnRestart = new[] { "port", "httpEnabled", "tlsEnabled", "tlsPort" },
                },
                webui = new { port = w.Port, requireHttps = w.RequireHttps, appliesOnRestart = new[] { "port", "requireHttps" } },
                spool = new { directory = sp.Directory, workerCount = sp.WorkerCount, appliesOnRestart = new[] { "directory", "workerCount" } },
            });
        });

        // PUT /api/config/{section} (spec §9.3, §12.5): persist the section's keys to SQL and refresh the
        // ConfigCache so live settings take effect within this request. Restart-bound keys (ports, spool,
        // TLS) are stored now and applied on the next restart.
        group.MapPut("/config/listener", async (ListenerConfigDto d, IConfigRepository config, ConfigCache cache, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            if (d.Ports is { } ports) await config.SetAsync(ConfigKeys.ListenerPorts, JsonSerializer.Serialize(ports), false, ct);
            if (d.ServerName is { } sn) await config.SetAsync(ConfigKeys.ListenerServerName, sn, false, ct);
            if (d.AllowedCidrs is { } c) await config.SetAsync(ConfigKeys.ListenerAllowedCidrs, JsonSerializer.Serialize(c), false, ct);
            if (d.MaxMessageBytes is { } m) await config.SetAsync(ConfigKeys.ListenerMaxMessageBytes, m.ToString(CultureInfo.InvariantCulture), false, ct);
            if (d.RequireAuth is { } ra) await config.SetAsync(ConfigKeys.ListenerRequireAuth, ra ? "true" : "false", false, ct);
            if (d.TlsCertPath is { } tp) await config.SetAsync(ConfigKeys.ListenerTlsCertPath, tp, false, ct);
            if (d.TlsCertPassword is { } tpw) await config.SetAsync(ConfigKeys.ListenerTlsCertPassword, tpw, encrypted: true, ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "SMTP listener settings updated", "Notice", "admin", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true });
        });

        // Shared TLS cert management — operators generate a self-signed cert or upload a cert + key (no file
        // paths). It secures both the SMTP listener (STARTTLS) and the HTTPS ingestion API; both load it at
        // startup, so changes take effect after a service restart.
        group.MapPost("/config/tls-cert/generate", async (ConfigCache cache, IConfigRepository config, IWebHostEnvironment env, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            var cn = cache.Listener().ServerName is { Length: > 0 } s ? s : "Dispatch";
            var (path, pw) = TlsCert.Generate(env.ContentRootPath, cn);
            await SetCertAsync(config, path, pw, "generated", ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "TLS certificate generated (self-signed)", "Notice", "admin", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true, source = "generated" });
        });

        group.MapPost("/config/tls-cert/upload", async (HttpRequest req, ConfigCache cache, IConfigRepository config, IWebHostEnvironment env, IAuditLog audit, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected a multipart upload with 'cert' and 'key' files." });
            var form = await req.ReadFormAsync(ct);
            var certFile = form.Files["cert"];
            var keyFile = form.Files["key"];
            if (certFile is null || keyFile is null) return Results.BadRequest(new { error = "Both a 'cert' and a 'key' PEM file are required." });
            string certPem, keyPem;
            using (var r = new StreamReader(certFile.OpenReadStream())) certPem = await r.ReadToEndAsync(ct);
            using (var r = new StreamReader(keyFile.OpenReadStream())) keyPem = await r.ReadToEndAsync(ct);
            string path, pw;
            try { (path, pw) = TlsCert.FromPem(env.ContentRootPath, certPem, keyPem); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid certificate or key: {ex.Message}" }); }
            await SetCertAsync(config, path, pw, "uploaded", ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "TLS certificate uploaded", "Notice", "admin", req.HttpContext.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true, source = "uploaded" });
        });

        group.MapDelete("/config/tls-cert", async (ConfigCache cache, IConfigRepository config, IWebHostEnvironment env, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            TlsCert.Delete(env.ContentRootPath);
            await SetCertAsync(config, "", "", "", ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "TLS certificate removed", "Notice", "admin", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true });
        });

        group.MapPut("/config/api", async (ApiConfigDto d, IConfigRepository config, ConfigCache cache, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            if (d.Port is { } p) await config.SetAsync(ConfigKeys.ApiPort, p.ToString(CultureInfo.InvariantCulture), false, ct);
            if (d.HttpEnabled is { } he) await config.SetAsync(ConfigKeys.ApiHttpEnabled, he ? "true" : "false", false, ct);
            if (d.TlsEnabled is { } te) await config.SetAsync(ConfigKeys.ApiTlsEnabled, te ? "true" : "false", false, ct);
            if (d.TlsPort is { } tp) await config.SetAsync(ConfigKeys.ApiTlsPort, tp.ToString(CultureInfo.InvariantCulture), false, ct);
            if (d.AllowedCidrs is { } c) await config.SetAsync(ConfigKeys.ApiAllowedCidrs, JsonSerializer.Serialize(c), false, ct);
            if (d.MaxMessageBytes is { } m) await config.SetAsync(ConfigKeys.ApiMaxMessageBytes, m.ToString(CultureInfo.InvariantCulture), false, ct);
            if (d.RateLimitPerKey is { } r) await config.SetAsync(ConfigKeys.ApiRateLimitPerKey, r.ToString(CultureInfo.InvariantCulture), false, ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "HTTP API settings updated", "Notice", "admin", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true });
        });

        group.MapPut("/config/webui", async (WebUiConfigDto d, IConfigRepository config, ConfigCache cache, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            if (d.Port is { } p) await config.SetAsync(ConfigKeys.WebUiPort, p.ToString(CultureInfo.InvariantCulture), false, ct);
            if (d.AllowedCidrs is { } c) await config.SetAsync(ConfigKeys.WebUiAllowedCidrs, JsonSerializer.Serialize(c), false, ct);
            if (d.RequireHttps is { } rh) await config.SetAsync(ConfigKeys.WebUiRequireHttps, rh ? "true" : "false", false, ct);
            if (d.SessionTimeoutMinutes is { } s) await config.SetAsync(ConfigKeys.WebUiSessionTimeoutMinutes, s.ToString(CultureInfo.InvariantCulture), false, ct);
            await cache.LoadAsync(config, ct);
            await audit.Audit("Config", "Dashboard settings updated", "Notice", "admin", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true });
        });

        group.MapPut("/config/spool", async (SpoolConfigDto d, IConfigRepository config, ConfigCache cache, CancellationToken ct) =>
        {
            if (d.Directory is { } dir) await config.SetAsync(ConfigKeys.SpoolDirectory, dir, false, ct);
            if (d.WorkerCount is { } w) await config.SetAsync(ConfigKeys.SpoolWorkerCount, w.ToString(CultureInfo.InvariantCulture), false, ct);
            await cache.LoadAsync(config, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/settings", async (
            IConfigRepository config,
            IOptions<RetryOptions> retryDefaults,
            IOptions<PurgeOptions> purgeDefaults,
            CancellationToken ct) =>
        {
            var rd = retryDefaults.Value;
            var pd = purgeDefaults.Value;
            return Results.Ok(new
            {
                logging = new
                {
                    delivered = await IsOn(config, SqlLoggingSettings.LogDeliveredKey, ct),
                    retrying = await IsOn(config, SqlLoggingSettings.LogRetryingKey, ct),
                    denied = await IsOn(config, SqlLoggingSettings.LogDeniedKey, ct),
                },
                retry = new
                {
                    maxRetries = await ReadInt(config, SqlRetrySettings.MaxRetriesKey, rd.MaxRetries, ct),
                    retryDelaysSeconds = await ReadDelays(config, rd.EffectiveDelaysSeconds, ct),
                },
                retention = new
                {
                    logDeliveredRetentionDays = await ReadInt(config, SqlPurgeSettings.LogDeliveredRetentionDaysKey, pd.Log.DeliveredRetentionDays, ct),
                    logFailedRetentionDays = await ReadInt(config, SqlPurgeSettings.LogFailedRetentionDaysKey, pd.Log.FailedRetentionDays, ct),
                    spoolFailedRetentionDays = await ReadInt(config, SqlPurgeSettings.SpoolFailedRetentionDaysKey, pd.SpoolFailedRetentionDays, ct),
                    capturedRetentionDays = await ReadInt(config, SqlPurgeSettings.CapturedRetentionDaysKey, pd.CapturedRetentionDays, ct),
                    sizeTriggerGb = await ReadDouble(config, SqlPurgeSettings.SizeTriggerGbKey, pd.SizePressure.TriggerGb, ct),
                    sizeTargetGb = await ReadDouble(config, SqlPurgeSettings.SizeTargetGbKey, pd.SizePressure.TargetGb, ct),
                },
            });
        });

        group.MapPut("/settings", async (UpdateSettingsRequest req, IConfigRepository config, CancellationToken ct) =>
        {
            if (req.Logging is { } l)
            {
                if (l.Delivered is { } d) await SetBool(config, SqlLoggingSettings.LogDeliveredKey, d, ct);
                if (l.Retrying is { } r) await SetBool(config, SqlLoggingSettings.LogRetryingKey, r, ct);
                if (l.Denied is { } n) await SetBool(config, SqlLoggingSettings.LogDeniedKey, n, ct);
            }

            if (req.Retry is { } rt)
            {
                if (rt.MaxRetries is { } mr) await SetInt(config, SqlRetrySettings.MaxRetriesKey, mr, ct);
                if (rt.RetryDelaysSeconds is { } delays)
                    await config.SetAsync(SqlRetrySettings.RetryDelaysSecondsKey, JsonSerializer.Serialize(delays), encrypted: false, ct);
            }

            if (req.Retention is { } re)
            {
                if (re.LogDeliveredRetentionDays is { } v1) await SetInt(config, SqlPurgeSettings.LogDeliveredRetentionDaysKey, v1, ct);
                if (re.LogFailedRetentionDays is { } v2) await SetInt(config, SqlPurgeSettings.LogFailedRetentionDaysKey, v2, ct);
                if (re.SpoolFailedRetentionDays is { } v3) await SetInt(config, SqlPurgeSettings.SpoolFailedRetentionDaysKey, v3, ct);
                if (re.CapturedRetentionDays is { } v4) await SetInt(config, SqlPurgeSettings.CapturedRetentionDaysKey, v4, ct);
                if (re.SizeTriggerGb is { } v5) await SetDouble(config, SqlPurgeSettings.SizeTriggerGbKey, v5, ct);
                if (re.SizeTargetGb is { } v6) await SetDouble(config, SqlPurgeSettings.SizeTargetGbKey, v6, ct);
            }

            return Results.Ok(new { ok = true });
        });
    }

    private static async Task<bool> IsOn(IConfigRepository config, string key, CancellationToken ct)
    {
        var v = await config.GetAsync(key, ct);
        return v is null || !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> ReadInt(IConfigRepository config, string key, int fallback, CancellationToken ct)
    {
        var v = await config.GetAsync(key, ct);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static async Task<double> ReadDouble(IConfigRepository config, string key, double fallback, CancellationToken ct)
    {
        var v = await config.GetAsync(key, ct);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static async Task<double[]> ReadDelays(IConfigRepository config, double[] fallback, CancellationToken ct)
    {
        var v = await config.GetAsync(SqlRetrySettings.RetryDelaysSecondsKey, ct);
        if (v is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<double[]>(v);
                if (parsed is { Length: > 0 }) return parsed;
            }
            catch (JsonException) { /* malformed — fall back */ }
        }
        return fallback;
    }

    private static Task SetBool(IConfigRepository config, string key, bool value, CancellationToken ct) =>
        config.SetAsync(key, value ? "true" : "false", encrypted: false, ct);

    private static Task SetInt(IConfigRepository config, string key, int value, CancellationToken ct) =>
        config.SetAsync(key, value.ToString(CultureInfo.InvariantCulture), encrypted: false, ct);

    private static Task SetDouble(IConfigRepository config, string key, double value, CancellationToken ct) =>
        config.SetAsync(key, value.ToString(CultureInfo.InvariantCulture), encrypted: false, ct);

    private static async Task SetCertAsync(IConfigRepository config, string path, string password, string source, CancellationToken ct)
    {
        await config.SetAsync(ConfigKeys.TlsCertPath, path, encrypted: false, ct);
        await config.SetAsync(ConfigKeys.TlsCertPassword, password, encrypted: true, ct);
        await config.SetAsync(ConfigKeys.TlsCertSource, source, encrypted: false, ct);
    }

    private sealed record UpdateSettingsRequest(LoggingToggles? Logging, RetrySettingsDto? Retry, RetentionSettingsDto? Retention);
    private sealed record LoggingToggles(bool? Delivered, bool? Retrying, bool? Denied);
    private sealed record RetrySettingsDto(int? MaxRetries, double[]? RetryDelaysSeconds);
    private sealed record RetentionSettingsDto(
        int? LogDeliveredRetentionDays,
        int? LogFailedRetentionDays,
        int? SpoolFailedRetentionDays,
        int? CapturedRetentionDays,
        double? SizeTriggerGb,
        double? SizeTargetGb);

    private sealed record ListenerConfigDto(
        int[]? Ports, string? ServerName, string[]? AllowedCidrs, long? MaxMessageBytes,
        bool? RequireAuth, string? TlsCertPath, string? TlsCertPassword);
    private sealed record ApiConfigDto(
        int? Port, bool? HttpEnabled, bool? TlsEnabled, int? TlsPort,
        string[]? AllowedCidrs, long? MaxMessageBytes, int? RateLimitPerKey);
    private sealed record WebUiConfigDto(int? Port, string[]? AllowedCidrs, bool? RequireHttps, int? SessionTimeoutMinutes);
    private sealed record SpoolConfigDto(string? Directory, int? WorkerCount);
}
