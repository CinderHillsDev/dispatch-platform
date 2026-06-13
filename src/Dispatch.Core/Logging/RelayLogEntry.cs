namespace Dispatch.Core.Logging;

/// <summary>
/// A single relay_log event (spec §6.11). Written after-the-fact, never on the receive path.
/// Denormalised relay/rule names so history survives renames and deletes.
/// </summary>
public sealed class RelayLogEntry
{
    // Event
    public string Event { get; init; } = "";          // Received | Delivered | Retrying | Failed | TestSent | Denied
    public string Status { get; init; } = "";         // OK | Error | Denied
    public int RetryAttempt { get; init; }

    // Identity
    public string SpoolId { get; init; } = "";

    // Envelope
    public string FromAddress { get; init; } = "";
    public string FromDomain { get; init; } = "";
    public IReadOnlyList<string> ToAddresses { get; init; } = [];
    public string ToDomain { get; init; } = "";
    public string? Subject { get; init; }
    public int SizeBytes { get; init; }

    // Routing
    public int? RelayId { get; init; }
    public string? RelayName { get; init; }
    public int? RoutingRuleId { get; init; }
    public string? RoutingRuleName { get; init; }
    public bool RoutingMatched { get; init; }

    // Provider outcome
    public string? Provider { get; init; }
    public string? ProviderMessageId { get; init; }
    public string? ProviderResponse { get; init; }
    public int? DurationMs { get; init; }
    public string? Error { get; init; }

    // Ingest source
    public string IngestSource { get; init; } = "SMTP";
    public string? SourceIp { get; init; }
    public int? ApiKeyId { get; init; }
    public string? ApiKeyName { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
