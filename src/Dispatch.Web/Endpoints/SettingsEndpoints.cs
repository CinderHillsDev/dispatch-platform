using Dispatch.Core.Configuration;
using Dispatch.Data.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Live settings stored in the SQL config table (spec §12). Currently the relay_log suppression toggles
/// (counters are always written; these only control whether log rows are inserted). Changes take effect
/// within the settings cache TTL — no restart.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (IConfigRepository config, CancellationToken ct) => Results.Ok(new
        {
            logging = new
            {
                delivered = await IsOn(config, SqlLoggingSettings.LogDeliveredKey, ct),
                retrying = await IsOn(config, SqlLoggingSettings.LogRetryingKey, ct),
                denied = await IsOn(config, SqlLoggingSettings.LogDeniedKey, ct),
            },
        }));

        group.MapPut("/settings", async (UpdateSettingsRequest req, IConfigRepository config, CancellationToken ct) =>
        {
            if (req.Logging is { } l)
            {
                if (l.Delivered is { } d) await Set(config, SqlLoggingSettings.LogDeliveredKey, d, ct);
                if (l.Retrying is { } r) await Set(config, SqlLoggingSettings.LogRetryingKey, r, ct);
                if (l.Denied is { } n) await Set(config, SqlLoggingSettings.LogDeniedKey, n, ct);
            }
            return Results.Ok(new { ok = true });
        });
    }

    private static async Task<bool> IsOn(IConfigRepository config, string key, CancellationToken ct)
    {
        var v = await config.GetAsync(key, ct);
        return v is null || !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static Task Set(IConfigRepository config, string key, bool value, CancellationToken ct) =>
        config.SetAsync(key, value ? "true" : "false", encrypted: false, ct);

    private sealed record UpdateSettingsRequest(LoggingToggles? Logging);
    private sealed record LoggingToggles(bool? Delivered, bool? Retrying, bool? Denied);
}
