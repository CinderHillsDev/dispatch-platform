using Dispatch.Core.Logging;

namespace Dispatch.Web.Realtime;

/// <summary>Compact relay event pushed to the dashboard's live activity feed (spec §9.2).</summary>
public sealed record RelayEventDto(
    DateTime LoggedAt,
    string Event,
    string Status,
    string SpoolId,
    string FromAddress,
    string ToDomain,
    string? Subject,
    string? RelayName,
    string? Provider,
    int? DurationMs,
    string IngestSource)
{
    public static RelayEventDto From(RelayLogEntry e) => new(
        DateTime.UtcNow, e.Event, e.Status, e.SpoolId, e.FromAddress, e.ToDomain,
        e.Subject, e.RelayName, e.Provider, e.DurationMs, e.IngestSource);
}
