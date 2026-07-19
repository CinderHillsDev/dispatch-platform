using System.Text.Json;
using Dispatch.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Inserts <c>relay_log</c> rows (spec §6.11). The after-the-fact event history.
///
/// This is the highest-volume write in the system: one row per lifecycle event per message, from every
/// spool worker thread at once. A fresh context per insert is deliberate - DbContext is not thread-safe,
/// and a short-lived one has no change-tracking state to accumulate.
/// </summary>
public sealed class SqlLogRepository(IDbContextFactory<DispatchDbContext> contexts) : ILogRepository
{
    public async Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);

        db.RelayLog.Add(new RelayLogEntity
        {
            // Stamped here, NOT left to the column default.
            //
            // SQLite stores timestamps as text and its CURRENT_TIMESTAMP has whole-second precision, while
            // a DateTime written through EF carries sub-second digits. Within the same second the shorter
            // string sorts FIRST, so a row the database stamped would appear OLDER than one inserted
            // moments before it - and the Message Log, which orders by (logged_at DESC, id DESC), would
            // show them out of order. Setting it explicitly gives every row identical precision.
            LoggedAt = DateTime.UtcNow,
            SpoolId = entry.SpoolId,
            Event = entry.Event,
            Status = entry.Status,
            RetryAttempt = entry.RetryAttempt,
            // The lengths mirror the column widths. SQLite does not enforce them and the other engines
            // would throw, so truncating here keeps behaviour identical across backends: an over-long
            // subject shortens the log entry rather than losing the whole row.
            FromAddress = Trunc(entry.FromAddress, 512) ?? "",
            FromDomain = Trunc(entry.FromDomain, 255) ?? "",
            ToAddresses = JsonSerializer.Serialize(entry.ToAddresses),
            ToDomain = Trunc(entry.ToDomain, 255) ?? "",
            Subject = Trunc(entry.Subject ?? "", 998) ?? "",
            SizeBytes = entry.SizeBytes,
            RelayId = entry.RelayId,
            RelayName = Trunc(entry.RelayName, 128),
            RoutingRuleId = entry.RoutingRuleId,
            RoutingRuleName = Trunc(entry.RoutingRuleName, 128),
            RoutingMatched = entry.RoutingMatched,
            Provider = Trunc(entry.Provider, 64),
            ProviderMessageId = Trunc(entry.ProviderMessageId, 256),
            ProviderResponse = entry.ProviderResponse,
            DurationMs = entry.DurationMs,
            Error = entry.Error,
            IngestSource = Trunc(entry.IngestSource, 16) ?? "SMTP",
            SourceIp = Trunc(entry.SourceIp, 64),
            ApiKeyId = entry.ApiKeyId,
            ApiKeyName = Trunc(entry.ApiKeyName, 256),
            Tags = entry.Tags is { Count: > 0 } ? JsonSerializer.Serialize(entry.Tags) : null,
            XMailer = Trunc(entry.XMailer, 256),
            AttachmentCount = entry.AttachmentCount,
        });

        await db.SaveChangesAsync(ct);
    }

    private static string? Trunc(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
