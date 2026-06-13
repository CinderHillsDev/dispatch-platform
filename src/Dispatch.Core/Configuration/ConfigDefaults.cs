namespace Dispatch.Core.Configuration;

/// <summary>
/// First-run config seeding (spec §12.6): when the <c>config</c> table is missing a key, it is populated
/// with its default value so a freshly installed instance is immediately usable. Idempotent — existing
/// values (operator edits) are never overwritten. Secret keys (TLS passwords, relay credentials) are left
/// unset rather than seeded with an encrypted empty string.
/// </summary>
public static class ConfigDefaults
{
    private const string Localhost = "[\"127.0.0.1/32\",\"::1/128\"]";

    /// <summary>The default key/value pairs, all non-encrypted. Values are stored verbatim (JSON for arrays).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ConfigKeys.ListenerPorts] = "[2525]",
        [ConfigKeys.ListenerServerName] = "Dispatch",
        [ConfigKeys.ListenerAllowedCidrs] = Localhost,
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
        [ConfigKeys.ApiPort] = "8421",
        [ConfigKeys.ApiAllowedCidrs] = Localhost,
        [ConfigKeys.ApiMaxMessageBytes] = "0",
        [ConfigKeys.ApiRateLimitPerKey] = "100",

        [ConfigKeys.WebUiPort] = "8420",
        [ConfigKeys.WebUiAllowedCidrs] = Localhost,
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
