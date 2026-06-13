namespace Dispatch.Core.Logging;

/// <summary>Opaque keyset cursor — the (logged_at, id) of the last row returned (spec §9.2).</summary>
public sealed record MessageLogCursor(DateTime LoggedAt, long Id);

/// <summary>Filters for the Message Log query. All applied together; values are always parameterised (§17, §19).</summary>
public sealed class MessageLogFilter
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string[]? Statuses { get; init; }       // OK | Error | Denied
    public string? IngestSource { get; init; }     // SMTP | API
    public string? FromDomain { get; init; }
    public string? ToDomain { get; init; }
    public int Limit { get; init; } = 50;
    public MessageLogCursor? Cursor { get; init; }
}

/// <summary>A projected Message Log row for the UI.</summary>
public sealed class MessageLogRow
{
    public long Id { get; init; }
    public DateTime LoggedAt { get; init; }
    public string Event { get; init; } = "";
    public string Status { get; init; } = "";
    public string SpoolId { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string ToDomain { get; init; } = "";
    public string? Subject { get; init; }
    public string? RelayName { get; init; }
    public string? Provider { get; init; }
    public int? DurationMs { get; init; }
    public int SizeBytes { get; init; }
    public string IngestSource { get; init; } = "";
    public int RetryAttempt { get; init; }
    public string? Error { get; init; }
}

public sealed record MessageLogPage(IReadOnlyList<MessageLogRow> Rows, MessageLogCursor? NextCursor);

/// <summary>Keyset-paginated read of <c>relay_log</c> for the Message Log (spec §9.2, §19).</summary>
public interface IMessageLogQuery
{
    Task<MessageLogPage> QueryAsync(MessageLogFilter filter, CancellationToken ct = default);

    /// <summary>Most recent log row for a spool id, for delivery-status lookups (spec §7.4).</summary>
    Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, CancellationToken ct = default);
}
