using System.Globalization;
using System.Text.Json;
using Dispatch.Core.Configuration;
using Dispatch.Data.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        // Read-only effective listener / HTTP-API / web-UI configuration for the System/About page (spec §9.2,
        // §9.3 GET /api/config). These are sourced from appsettings and applied at startup, so they require a
        // service restart to change — they are surfaced here for visibility, with the TLS passphrase redacted.
        group.MapGet("/config", (
            IOptions<ListenerOptions> listener, IOptions<ApiOptions> api, IOptions<WebUiOptions> webui) =>
        {
            var l = listener.Value;
            var a = api.Value;
            var w = webui.Value;
            return Results.Ok(new
            {
                editableViaRestartOnly = true,
                listener = new
                {
                    ports = l.EffectivePorts,
                    serverName = l.ServerName,
                    allowedCidrs = l.EffectiveAllowedCidrs,
                    maxMessageBytes = l.MaxMessageBytes,
                    requireAuth = l.RequireAuth,
                    tlsEnabled = !string.IsNullOrWhiteSpace(l.TlsCertPath),
                    tlsCertPath = l.TlsCertPath,
                    tlsCertPassword = string.IsNullOrEmpty(l.TlsCertPassword) ? null : "********",
                },
                api = new
                {
                    port = a.Port,
                    allowedCidrs = a.EffectiveAllowedCidrs,
                    maxMessageBytes = a.MaxMessageBytes,
                    rateLimitPerKey = a.RateLimitPerKey,
                },
                webui = new
                {
                    port = w.Port,
                    requireHttps = w.RequireHttps,
                },
            });
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
}
