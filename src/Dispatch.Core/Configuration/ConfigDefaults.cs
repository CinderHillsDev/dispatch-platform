namespace Dispatch.Core.Configuration;

/// <summary>
/// First-run config seeding (spec §12.6): when the <c>config</c> table is missing a key, it is populated
/// with its default value so a freshly installed instance is immediately usable. Idempotent - existing
/// values (operator edits) are never overwritten. Secret keys (TLS passwords, relay credentials) are left
/// unset rather than seeded with an encrypted empty string.
/// </summary>
public static class ConfigDefaults
{
    // SMTP listener and ingestion API are CLOSED by default (spec §17.10): only listed source IPs may
    // connect and an empty list denies everyone. Both default to loopback + private ranges (RFC1918 +
    // IPv6 ULA) so same-host apps, private LANs and Docker networks work out of the box while the public
    // internet can't - to open them an operator adds 0.0.0.0/0 + ::/0 deliberately in Access Control.
    // The dashboard is the exception: it is password-protected and governed by its own middleware where
    // an empty list = allow all, so a headless/NAT'd server stays reachable for first login.
    private const string DashboardAllowAll = "[]";
    private const string PrivateRanges =
        "[\"127.0.0.1/32\",\"::1/128\",\"10.0.0.0/8\",\"172.16.0.0/12\",\"192.168.0.0/16\",\"fc00::/7\"]";

    /// <summary>The default key/value pairs, all non-encrypted. Values are stored verbatim (JSON for arrays).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ConfigKeys.ListenerPorts] = "[25, 587]",   // standard SMTP ports; listener falls back to 2525 if 25 is taken/unprivileged
        [ConfigKeys.ListenerServerName] = "Dispatch",
        [ConfigKeys.ListenerAllowedCidrs] = PrivateRanges,
        [ConfigKeys.ListenerMaxMessageBytes] = "26214400",   // 25 MiB default ceiling (0 = no limit)
        [ConfigKeys.ListenerRequireAuth] = "false",
        [ConfigKeys.ListenerAllowUnsecureAuth] = "false",   // AUTH only after STARTTLS by default (no plaintext creds)
        [ConfigKeys.ListenerConnectionTimeoutSeconds] = "60",
        [ConfigKeys.ListenerMaxConnections] = "100",

        // Shared TLS certificate (SMTP STARTTLS + HTTPS API); unset until an operator generates/uploads one.
        [ConfigKeys.TlsCertSource] = "",

        [ConfigKeys.SpoolDirectory] = "./.dispatch-spool",
        [ConfigKeys.SpoolWorkerCount] = "4",
        [ConfigKeys.SpoolMaxRetries] = "3",
        [ConfigKeys.SpoolRetryDelaysSeconds] = "[30,300,1800]",

        [ConfigKeys.ApiEnabled] = "true",
        [ConfigKeys.ApiPort] = "8025",
        [ConfigKeys.ApiHttpEnabled] = "true",
        [ConfigKeys.ApiTlsEnabled] = "false",
        [ConfigKeys.ApiTlsPort] = "8026",
        [ConfigKeys.ApiAllowedCidrs] = PrivateRanges,
        [ConfigKeys.ApiMaxMessageBytes] = "26214400",   // 25 MiB - bounds in-memory buffering of HTTP uploads (0 = no limit)
        [ConfigKeys.ApiRateLimitPerKey] = "100",

        [ConfigKeys.WebUiPort] = "8420",
        [ConfigKeys.WebUiAllowedCidrs] = DashboardAllowAll,
        [ConfigKeys.WebUiSessionTimeoutMinutes] = "480",
        [ConfigKeys.WebUiRequireHttps] = "true",

        [ConfigKeys.LoggingLogDelivered] = "true",
        [ConfigKeys.LoggingLogRetrying] = "true",
        [ConfigKeys.LoggingLogDenied] = "true",

        [ConfigKeys.PurgeEnabled] = "true",
        [ConfigKeys.PurgeScheduleIntervalHours] = "6",
        [ConfigKeys.PurgeSpoolFailedRetentionDays] = "7",
        [ConfigKeys.PurgeCapturedRetentionDays] = "7",
        [ConfigKeys.PurgeLogDeliveredRetentionDays] = "7",
        [ConfigKeys.PurgeLogFailedRetentionDays] = "7",
        [ConfigKeys.PurgeAuditRetentionDays] = "7",
        [ConfigKeys.PurgeAuditSecurityRetentionDays] = "7",
        [ConfigKeys.PurgeArchiveRetentionDays] = "0",   // size-pressure JSONL archives: 0 = keep forever
        [ConfigKeys.PurgeSizeTriggerGb] = "0",   // 0 = size-pressure disabled (Postgres has no hard size cap; opt-in)
        [ConfigKeys.PurgeSizeTargetGb] = "0",
        [ConfigKeys.UpdatesSelfManaged] = "false",   // installers flip this on where a platform updater ships
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
