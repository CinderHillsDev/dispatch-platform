namespace Dispatch.Core.Configuration;

/// <summary>Retention + auto-purge settings (spec §6.10), bound from the "Purge" config section.</summary>
public sealed class PurgeOptions
{
    public const string SectionName = "Purge";

    public bool Enabled { get; set; } = true;
    public double ScheduleIntervalHours { get; set; } = 6;

    /// <summary>spool/failed/ files older than this are deleted.</summary>
    public int SpoolFailedRetentionDays { get; set; } = 30;

    /// <summary>spool/captured/ (local dev inbox) files older than this are deleted.</summary>
    public int CapturedRetentionDays { get; set; } = 7;

    /// <summary>audit_log entries older than this are deleted (0 = keep forever).</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>Noisier security audit events (allow-list denials, SMTP auth failures) kept this long (0 = forever).</summary>
    public int AuditSecurityRetentionDays { get; set; } = 7;

    public LogRetention Log { get; set; } = new();
    public SizePressureOptions SizePressure { get; set; } = new();

    public sealed class LogRetention
    {
        public int DeliveredRetentionDays { get; set; } = 30;
        public int FailedRetentionDays { get; set; } = 90;
        public int RetryingRetentionDays { get; set; } = 90;
        public int TestSentRetentionDays { get; set; } = 7;
    }

    public sealed class SizePressureOptions
    {
        public double TriggerGb { get; set; } = 9.5;   // SQL Server Express 10 GB limit − 0.5 GB buffer
        public double TargetGb { get; set; } = 9.0;
    }
}
