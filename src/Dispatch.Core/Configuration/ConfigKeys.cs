namespace Dispatch.Core.Configuration;

/// <summary>
/// Canonical SQL <c>config</c>-table keys (spec §12.3). All Dispatch settings except the two bootstrap
/// items in appsettings (the DB connection string and the Web UI TLS cert path/password) live here.
/// </summary>
public static class ConfigKeys
{
    // Listener (SMTP)
    public const string ListenerPorts = "listener.ports";                       // JSON int[]
    public const string ListenerServerName = "listener.server_name";
    public const string ListenerAllowedCidrs = "listener.allowed_cidrs";        // JSON string[]
    public const string ListenerMaxMessageBytes = "listener.max_message_bytes";
    public const string ListenerRequireAuth = "listener.require_auth";
    public const string ListenerAllowUnsecureAuth = "listener.allow_unsecure_auth"; // AUTH over plaintext (no STARTTLS)
    public const string ListenerTlsCertPath = "listener.tls_cert_path";         // deprecated - see Tls* (shared cert)
    public const string ListenerTlsCertPassword = "listener.tls_cert_password"; // deprecated
    public const string ListenerTlsCertSource = "listener.tls_cert_source";     // deprecated
    public const string ListenerConnectionTimeoutSeconds = "listener.connection_timeout_seconds";
    public const string ListenerMaxConnections = "listener.max_connections";

    // Shared TLS certificate - secures both the SMTP listener (STARTTLS) and the HTTPS ingestion API.
    // (The dashboard keeps its own cert in appsettings / an auto self-signed cert.)
    public const string TlsCertPath = "tls.cert_path";
    public const string TlsCertPassword = "tls.cert_password";                  // encrypted
    public const string TlsCertSource = "tls.cert_source";                      // "generated" | "uploaded" | ""

    // Spool / worker
    public const string SpoolDirectory = "spool.directory";
    public const string SpoolWorkerCount = "spool.worker_count";
    public const string SpoolMaxRetries = "spool.max_retries";
    public const string SpoolRetryDelaysSeconds = "spool.retry_delays_seconds"; // JSON double[]

    // HTTP ingestion API
    public const string ApiEnabled = "api.enabled";
    public const string ApiPort = "api.port";
    public const string ApiHttpEnabled = "api.http_enabled";                    // listen on the plain-HTTP port
    public const string ApiTlsEnabled = "api.tls_enabled";                      // also listen on an HTTPS port
    public const string ApiTlsPort = "api.tls_port";
    public const string ApiAllowedCidrs = "api.allowed_cidrs";                  // JSON string[]
    public const string ApiMaxMessageBytes = "api.max_message_bytes";
    public const string ApiRateLimitPerKey = "api.rate_limit_per_key";

    // Web UI
    public const string WebUiPort = "webui.port";
    public const string WebUiAllowedCidrs = "webui.allowed_cidrs";              // JSON string[]
    public const string WebUiSessionTimeoutMinutes = "webui.session_timeout_minutes";
    public const string WebUiRequireHttps = "webui.require_https";
    // Monotonic credential epoch: bumped on every admin-password change so existing cookies (which carry the
    // epoch they were issued under) are rejected - i.e. changing the password signs out all other sessions.
    public const string WebUiSessionEpoch = "webui.session_epoch";

    // Logging suppression toggles (also referenced by SqlLoggingSettings)
    public const string LoggingLogDelivered = "logging.log_delivered";
    public const string LoggingLogRetrying = "logging.log_retrying";
    public const string LoggingLogDenied = "logging.log_denied";

    // Purge / retention (also referenced by SqlPurgeSettings)
    public const string PurgeEnabled = "purge.enabled";
    public const string PurgeScheduleIntervalHours = "purge.schedule_interval_hours";
    public const string PurgeSpoolFailedRetentionDays = "purge.spool_failed_retention_days";
    public const string PurgeCapturedRetentionDays = "purge.captured_retention_days";
    public const string PurgeLogDeliveredRetentionDays = "purge.log_delivered_retention_days";
    public const string PurgeLogFailedRetentionDays = "purge.log_failed_retention_days";
    public const string PurgeAuditRetentionDays = "purge.audit_retention_days";
    public const string PurgeAuditSecurityRetentionDays = "purge.audit_security_retention_days";
    public const string PurgeArchiveRetentionDays = "purge.archive_retention_days";
    public const string PurgeSizeTriggerGb = "purge.size_trigger_gb";
    public const string PurgeSizeTargetGb = "purge.size_target_gb";

    // Set true by installers that ship a platform updater (appliance / Linux / Windows), enabling the
    // web-UI "upload an upgrade package" flow. False (e.g. Docker) hides/refuses in-app updates.
    public const string UpdatesSelfManaged = "updates.self_managed";
}
