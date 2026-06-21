namespace Dispatch.Core.Configuration;

/// <summary>
/// First-run config seeding (spec §12.6): when the <c>config</c> table is missing a key, it is populated
/// with its default value so a freshly installed instance is immediately usable. Idempotent — existing
/// values (operator edits) are never overwritten. Secret keys (TLS passwords, relay credentials) are left
/// unset rather than seeded with an encrypted empty string.
/// </summary>
public static class ConfigDefaults
{
    // The dashboard (password-protected) and ingestion API (API-key protected) default to an empty
    // allow-list = allow all, so a headless server or a NAT'd container is reachable out of the box;
    // operators tighten the source-IP allow-list in the dashboard. The SMTP listener is NOT auth-gated
    // by default, so to avoid shipping an open relay it defaults to loopback + private ranges (RFC1918 +
    // IPv6 ULA): same-host apps, private LANs and Docker networks can submit; the public internet can't.
    private const string AllowAll = "[]";
    private const string PrivateRanges =
        "[\"127.0.0.1/32\",\"::1/128\",\"10.0.0.0/8\",\"172.16.0.0/12\",\"192.168.0.0/16\",\"fc00::/7\"]";

    /// <summary>The default key/value pairs, all non-encrypted. Values are stored verbatim (JSON for arrays).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ConfigKeys.ListenerPorts] = "[2525]",
        [ConfigKeys.ListenerServerName] = "Dispatch",
        [ConfigKeys.ListenerAllowedCidrs] = PrivateRanges,
        [ConfigKeys.ListenerMaxMessageBytes] = "0",
        [ConfigKeys.ListenerRequireAuth] = "false",
        [ConfigKeys.ListenerTlsCertPath] = "",
        [ConfigKeys.ListenerConnectionTimeoutSeconds] = "60",
        [ConfigKeys.ListenerMaxConnections] = "100",

        [ConfigKeys.SpoolDirectory] = "./.dispatch-spool",
        [ConfigKeys.SpoolWorkerCount] = "4",
        [ConfigKeys.SpoolMaxRetries] = "3",
        [ConfigKeys.SpoolRetryDelaysSeconds] = "[30,300,1800]",

        [ConfigKeys.ApiEnabled] = "true",
        [ConfigKeys.ApiPort] = "8025",
        [ConfigKeys.ApiAllowedCidrs] = AllowAll,
        [ConfigKeys.ApiMaxMessageBytes] = "26214400",   // 25 MiB — bounds in-memory buffering of HTTP uploads (0 = no limit)
        [ConfigKeys.ApiRateLimitPerKey] = "100",

        [ConfigKeys.WebUiPort] = "8420",
        [ConfigKeys.WebUiAllowedCidrs] = AllowAll,
        [ConfigKeys.WebUiSessionTimeoutMinutes] = "480",
        [ConfigKeys.WebUiRequireHttps] = "false",

        [ConfigKeys.LoggingLogDelivered] = "true",
        [ConfigKeys.LoggingLogRetrying] = "true",
        [ConfigKeys.LoggingLogDenied] = "true",

        [ConfigKeys.PurgeEnabled] = "true",
        [ConfigKeys.PurgeScheduleIntervalHours] = "6",
        [ConfigKeys.PurgeSpoolFailedRetentionDays] = "30",
        [ConfigKeys.PurgeCapturedRetentionDays] = "7",
        [ConfigKeys.PurgeLogDeliveredRetentionDays] = "30",
        [ConfigKeys.PurgeLogFailedRetentionDays] = "90",
        [ConfigKeys.PurgeSizeTriggerGb] = "9.5",
        [ConfigKeys.PurgeSizeTargetGb] = "9.0",
    };

    /// <summary>Inserts any missing default key. Existing keys are left untouched.</summary>
    public static async Task SeedAsync(IConfigRepository repo, CancellationToken ct = default)
    {
        var existing = (await repo.GetAllAsync(ct)).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Defaults)
        {
            if (existing.Contains(key)) continue;
            await repo.SetAsync(key, value, encrypted: false, ct);
        }
    }
}
