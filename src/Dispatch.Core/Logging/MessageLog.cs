namespace Dispatch.Core.Logging;

/// <summary>Opaque keyset cursor — the (logged_at, id) of the last row returned (spec §9.2).</summary>
public sealed record MessageLogCursor(DateTime LoggedAt, long Id);

/// <summary>Filters for the Message Log query. All applied together; values are always parameterised (§17, §19).</summary>
public sealed class MessageLogFilter
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string[]? Statuses { get; init; }       // OK | Error | Denied (status column)
    public string[]? Events { get; init; }         // Delivered | Failed | Retrying | Denied | TestSent (event column, §9.2 chips)
    public string? IngestSource { get; init; }     // SMTP | API
    public string? FromDomain { get; init; }
    public string? ToDomain { get; init; }
    public string? RelayName { get; init; }
    public string? RoutingRuleName { get; init; }  // restrict to messages routed by a named rule (§9.2)
    public string? Subject { get; init; }          // case-insensitive substring match on subject (§9.2)
    public string? Tag { get; init; }
    public int? ApiKeyId { get; init; }            // restrict to one API key (per-key list, §7.4)
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

/// <summary>Full per-message detail for the Message Log row-detail panel (spec §9.2).</summary>
public sealed class MessageLogDetail
{
    public long Id { get; init; }
    public DateTime LoggedAt { get; init; }
    public string Event { get; init; } = "";
    public string Status { get; init; } = "";
    public string SpoolId { get; init; } = "";
    public int RetryAttempt { get; init; }
    public string FromAddress { get; init; } = "";
    public string FromDomain { get; init; } = "";
    public IReadOnlyList<string> ToAddresses { get; init; } = [];
    public string ToDomain { get; init; } = "";
    public string? Subject { get; init; }
    public int SizeBytes { get; init; }
    public string? RelayName { get; init; }
    public string? RoutingRuleName { get; init; }
    public bool RoutingMatched { get; init; }
    public string? Provider { get; init; }
    public string? ProviderMessageId { get; init; }
    public string? ProviderResponse { get; init; }
    public int? DurationMs { get; init; }
    public string? Error { get; init; }
    public string IngestSource { get; init; } = "";
    public string? SourceIp { get; init; }
    public string? ApiKeyName { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>All relay_log rows for this spool id, oldest first — the retry/attempt timeline (spec §9.2).</summary>
    public IReadOnlyList<MessageLogAttempt> History { get; init; } = [];
}

/// <summary>One attempt in a message's retry history (spec §9.2 row-detail timeline).</summary>
public sealed record MessageLogAttempt(
    DateTime LoggedAt, string Event, string Status, int RetryAttempt, string? Provider, int? DurationMs, string? Error);

/// <summary>Keyset-paginated read of <c>relay_log</c> for the Message Log (spec §9.2, §19).</summary>
public interface IMessageLogQuery
{
    Task<MessageLogPage> QueryAsync(MessageLogFilter filter, CancellationToken ct = default);

    /// <summary>Most recent log row for a spool id, for delivery-status lookups (spec §7.4). When
    /// <paramref name="apiKeyId"/> is non-null the lookup is scoped to that key, so one key can't read
    /// another key's message status by guessing its id.</summary>
    Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, int? apiKeyId, CancellationToken ct = default);

    /// <summary>Recent log rows submitted with a given API key, newest first (per-key list, spec §7.4).</summary>
    Task<IReadOnlyList<MessageLogRow>> RecentByApiKeyAsync(int apiKeyId, int limit, string[]? statuses, CancellationToken ct = default);

    /// <summary>Full detail for a single log row by id, for the row-detail panel (spec §9.2). Null if missing.</summary>
    Task<MessageLogDetail?> GetByIdAsync(long id, CancellationToken ct = default);
}
