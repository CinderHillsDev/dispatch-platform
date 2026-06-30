namespace Dispatch.Core.Maintenance;

/// <summary>relay_log row count for a single event type (Delivered/Failed/Retrying/TestSent/Denied).</summary>
public sealed record LogEventCount(string Event, long Rows);

/// <summary>
/// Database-side storage usage, so operators can see what their retention windows are actually holding
/// (spec §6.10 companion to retention). Per-event byte figures aren't cheap to get exactly in SQL Server,
/// so the endpoint estimates them by apportioning the relay_log table size across the event row counts;
/// these raw facts are what that estimate is built from.
/// </summary>
public sealed record DbStorage(
    bool Connected,
    long DatabaseBytes,
    long RelayLogBytes,
    IReadOnlyList<LogEventCount> RelayLogByEvent,
    long AuditBytes,
    long AuditRows,
    long AuditSecurityRows);

/// <summary>Reports how much database space the logged/audited data is using, broken out by category.</summary>
public interface IStorageReport
{
    Task<DbStorage> GetAsync(CancellationToken ct = default);
}
