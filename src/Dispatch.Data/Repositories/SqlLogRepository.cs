using System.Text.Json;
using Dapper;
using Dispatch.Core.Logging;

namespace Dispatch.Data.Repositories;

/// <summary>Inserts <c>relay_log</c> rows (spec §6.11). The after-the-fact event history.</summary>
public sealed class SqlLogRepository(SqlConnectionFactory factory) : ILogRepository
{
    private const string Sql = """
        INSERT INTO relay_log
            (spool_id, event, status, retry_attempt, from_address, from_domain, to_addresses, to_domain,
             subject, size_bytes, relay_id, relay_name, routing_rule_id, routing_rule_name, routing_matched,
             provider, provider_message_id, provider_response, duration_ms, error, ingest_source, source_ip,
             api_key_id, api_key_name, tags, x_mailer, attachment_count)
        VALUES
            (@SpoolId, @Event, @Status, @RetryAttempt, @FromAddress, @FromDomain, @ToAddresses, @ToDomain,
             @Subject, @SizeBytes, @RelayId, @RelayName, @RoutingRuleId, @RoutingRuleName, @RoutingMatched,
             @Provider, @ProviderMessageId, @ProviderResponse, @DurationMs, @Error, @IngestSource, @SourceIp,
             @ApiKeyId, @ApiKeyName, @Tags, @XMailer, @AttachmentCount);
        """;

    public async Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(Sql, new
        {
            entry.SpoolId,
            entry.Event,
            entry.Status,
            entry.RetryAttempt,
            FromAddress = Trunc(entry.FromAddress, 512),
            FromDomain = Trunc(entry.FromDomain, 255),
            ToAddresses = JsonSerializer.Serialize(entry.ToAddresses),
            ToDomain = Trunc(entry.ToDomain, 255),
            Subject = Trunc(entry.Subject ?? "", 998),
            entry.SizeBytes,
            entry.RelayId,
            RelayName = Trunc(entry.RelayName, 128),
            entry.RoutingRuleId,
            RoutingRuleName = Trunc(entry.RoutingRuleName, 128),
            entry.RoutingMatched,
            Provider = Trunc(entry.Provider, 64),
            ProviderMessageId = Trunc(entry.ProviderMessageId, 256),
            entry.ProviderResponse,
            entry.DurationMs,
            entry.Error,
            IngestSource = Trunc(entry.IngestSource, 16),
            SourceIp = Trunc(entry.SourceIp, 64),
            entry.ApiKeyId,
            ApiKeyName = Trunc(entry.ApiKeyName, 256),
            Tags = entry.Tags is { Count: > 0 } ? JsonSerializer.Serialize(entry.Tags) : null,
            XMailer = Trunc(entry.XMailer, 256),
            entry.AttachmentCount,
        }, cancellationToken: ct));
    }

    private static string? Trunc(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
