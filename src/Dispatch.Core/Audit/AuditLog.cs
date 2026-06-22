namespace Dispatch.Core.Audit;

/// <summary>A single audit/security log entry shown on the System Logs page.</summary>
public sealed record AuditEntry(
    long Id, DateTime LoggedAt, string Kind, string Category, string Event,
    string Severity, string? Actor, string? SourceIp, string? Detail);

/// <summary>Cursor for keyset "load more" (newest-first).</summary>
public sealed record AuditCursor(DateTime LoggedAt, long Id);

/// <summary>Filters for the Logs query. Kind is null/"" for all, else "audit"|"relay"|"system";
/// Category/Severity are optional exact-match column filters; Search is a free-text contains.</summary>
public sealed record AuditFilter(string? Kind, string? Category, string? Severity, string? Search, int Limit, AuditCursor? Cursor);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Rows, AuditCursor? NextCursor);

/// <summary>
/// Append-only audit/security event log (spec §17). Writes are best-effort — a logging failure must never
/// break the action being audited, so implementations swallow errors.
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(string kind, string category, string @event, string severity,
        string? actor, string? sourceIp, string? detail, CancellationToken ct = default);

    Task<AuditPage> QueryAsync(AuditFilter filter, CancellationToken ct = default);
}

/// <summary>Ergonomic helpers so call sites read clearly and use consistent kinds/severities.</summary>
public static class AuditLogExtensions
{
    public static Task Audit(this IAuditLog log, string category, string @event,
        string severity = "Info", string? actor = null, string? sourceIp = null, string? detail = null, CancellationToken ct = default)
        => log.WriteAsync("audit", category, @event, severity, actor, sourceIp, detail, ct);

    /// <summary>A system-level problem — e.g. an unhandled server exception.</summary>
    public static Task System(this IAuditLog log, string @event, string? detail, string? sourceIp = null, CancellationToken ct = default)
        => log.WriteAsync("system", "System", @event, "Error", actor: null, sourceIp, detail, ct);

    /// <summary>A normal system lifecycle event (startup, scheduled cleanup, disk-pressure change).</summary>
    public static Task Lifecycle(this IAuditLog log, string @event, string? detail = null, string severity = "Info", CancellationToken ct = default)
        => log.WriteAsync("system", "System", @event, severity, actor: null, sourceIp: null, detail, ct);

    /// <summary>A relay/delivery problem (e.g. a provider rejected the message — bad API key, etc.).</summary>
    public static Task Relay(this IAuditLog log, string @event, string? detail, string severity = "Error", CancellationToken ct = default)
        => log.WriteAsync("relay", "Relay", @event, severity, actor: null, sourceIp: null, detail, ct);
}
