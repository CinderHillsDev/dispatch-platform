namespace Dispatch.Core.Configuration;

/// <summary>Retention + auto-purge settings (spec §6.10), bound from the "Purge" config section.</summary>
public sealed class PurgeOptions
{
    public const string SectionName = "Purge";

    public bool Enabled { get; set; } = true;
    public double ScheduleIntervalHours { get; set; } = 6;

    /// <summary>spool/failed/ files older than this are deleted.</summary>
    public int SpoolFailedRetentionDays { get; set; } = 7;

    /// <summary>spool/captured/ (local dev inbox) files older than this are deleted.</summary>
    public int CapturedRetentionDays { get; set; } = 7;

    /// <summary>audit_log entries older than this are deleted (0 = keep forever).</summary>
    public int AuditRetentionDays { get; set; } = 7;

    /// <summary>Noisier security audit events (allow-list denials, SMTP auth failures) kept this long (0 = forever).</summary>
    public int AuditSecurityRetentionDays { get; set; } = 7;

    /// <summary>Weekly JSONL archive files written by the size-pressure purge are deleted after this
    /// many days (0 = keep forever - the default, since they're emergency exports).</summary>
    public int ArchiveRetentionDays { get; set; }

    public LogRetention Log { get; set; } = new();
    public SizePressureOptions SizePressure { get; set; } = new();

    public sealed class LogRetention
    {
        public int DeliveredRetentionDays { get; set; } = 7;
        public int FailedRetentionDays { get; set; } = 7;
        public int RetryingRetentionDays { get; set; } = 7;
        public int TestSentRetentionDays { get; set; } = 7;
    }

    public sealed class SizePressureOptions
    {
        // Optional physical DB-size cap (pg_database_size). 0 = disabled (the default): PostgreSQL has no
        // hard size limit, so size-pressure is opt-in. Set TriggerGb > 0 to have the purge archive + delete
        // the oldest history (then VACUUM FULL) once the database grows past TriggerGb, down to TargetGb.
        public double TriggerGb { get; set; }
        public double TargetGb { get; set; }
    }
}
