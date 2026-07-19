namespace Dispatch.Data;

/// <summary>
/// Persistence entities. These are storage shapes, deliberately separate from the domain records in
/// Dispatch.Core (RelayLogEntry, AuditEntry, CounterTotals, ...) that the repository interfaces expose.
/// Keeping them apart is what lets the schema carry columns the domain does not model - and lets the
/// domain evolve without a migration - at the cost of an explicit mapping in each repository.
///
/// All timestamps are UTC. Nullability mirrors the schema exactly.
/// </summary>
public sealed class ConfigEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Encrypted { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RelayEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; }
    public int MaxConcurrency { get; set; }
    public int MaxMessageBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RoutingRuleEntity
{
    public int Id { get; set; }
    public int Priority { get; set; }
    public string Name { get; set; } = "";
    public string? RecipientPattern { get; set; }
    public string? SenderPattern { get; set; }
    public int RelayId { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ApiKeyEntity
{
    public int Id { get; set; }
    public string KeyId { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public long MessageCount { get; set; }
    public bool Revoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int RateLimitPerMinute { get; set; }
    public string Scope { get; set; } = "send";
}

/// <summary>
/// Daily per-relay aggregates. <see cref="RelayId"/> 0 is the "no specific relay" bucket for
/// connection-level events (denials counted before routing) and is intentionally not a foreign key.
/// </summary>
public sealed class RelayCounterEntity
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int RelayId { get; set; }
    public long Received { get; set; }
    public long Delivered { get; set; }
    public long Failed { get; set; }
    public long Retried { get; set; }
    public long Denied { get; set; }
}

public sealed class RelayLogEntity
{
    public long Id { get; set; }
    public DateTime LoggedAt { get; set; }
    public string SpoolId { get; set; } = "";
    public string Event { get; set; } = "";
    public string Status { get; set; } = "";
    public int RetryAttempt { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromDomain { get; set; } = "";
    /// <summary>JSON array of recipient addresses; see MessageLogJson.</summary>
    public string ToAddresses { get; set; } = "[]";
    public string ToDomain { get; set; } = "";
    public string Subject { get; set; } = "";
    public int SizeBytes { get; set; }
    public int? RelayId { get; set; }
    public string? RelayName { get; set; }
    public int? RoutingRuleId { get; set; }
    public string? RoutingRuleName { get; set; }
    public bool RoutingMatched { get; set; }
    public string? Provider { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderResponse { get; set; }
    public int? DurationMs { get; set; }
    public string? Error { get; set; }
    public string IngestSource { get; set; } = "SMTP";
    public string? SourceIp { get; set; }
    public int? ApiKeyId { get; set; }
    public string? ApiKeyName { get; set; }
    /// <summary>JSON array of tags, or null when the message carried none.</summary>
    public string? Tags { get; set; }
    public string? XMailer { get; set; }
    public int AttachmentCount { get; set; }
}

public sealed class SmtpCredentialEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public sealed class AuditLogEntity
{
    public long Id { get; set; }
    public DateTime LoggedAt { get; set; }
    /// <summary>audit | error - the coarse filter the System Logs page exposes.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Auth | ApiKey | Config | Error.</summary>
    public string Category { get; set; } = "";
    public string Event { get; set; } = "";
    /// <summary>Info | Notice | Warning | Error.</summary>
    public string Severity { get; set; } = "Info";
    public string? Actor { get; set; }
    public string? SourceIp { get; set; }
    public string? Detail { get; set; }
}
