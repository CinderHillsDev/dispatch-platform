using Dispatch.Core.Logging;
using Dispatch.Core.Maintenance;
using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>relay_log purge operations (spec §6.10). Batched deletes with pauses to avoid lock contention.</summary>
public sealed class SqlLogMaintenance(
    IDbContextFactory<DispatchDbContext> contexts, IDatabaseProvider provider) : ILogMaintenance
{
    private const int BatchSize = 1000;

    /// <summary>
    /// Deletes aged rows for one event type, in bounded batches with a pause between them.
    ///
    /// The batching is not politeness. On SQLite there is a single database-wide write lock, so one
    /// unbounded DELETE over a large relay_log would hold it for the whole operation and stall every
    /// ingest worker behind it; the pause is what lets them in. It also keeps transaction sizes sane on the
    /// server engines. The retention cutoff is computed here rather than in SQL, which is what removes the
    /// need for engine-specific interval arithmetic.
    /// </summary>
    public async Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            // Select then delete by key: no provider supports a row limit on ExecuteDelete, and an
            // unbounded delete is precisely what the batching exists to prevent.
            var ids = await db.RelayLog.AsNoTracking()
                .Where(r => r.Event == @event && r.LoggedAt < cutoff)
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .Select(r => r.Id)
                .ToListAsync(ct);

            if (ids.Count == 0) break;

            total += await db.RelayLog.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) break;
            await Task.Delay(100, ct);   // breathe between batches
        }

        return total;
    }

    public async Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await provider.GetDatabaseSizeBytesAsync(db, ct);
    }

    public async Task<(long TableBytes, long RowCount)> GetRelayLogStatsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tableBytes = await provider.GetTableSizeBytesAsync(db, "relay_log", ct);
        var rowCount = await db.RelayLog.AsNoTracking().LongCountAsync(ct);
        return (tableBytes, rowCount);
    }

    public async Task<int> ArchiveAndDeleteOldestRelayLogAsync(int batch, ArchiveRows archive, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.RelayLog.AsNoTracking()
            .OrderBy(r => r.LoggedAt).ThenBy(r => r.Id)
            .Take(batch)
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        // Archive first; if that throws, the rows are NOT deleted. The safety net must not lose data.
        await archive(rows.Select(ToRow).ToList(), ct);

        var ids = rows.Select(r => r.Id).ToList();
        return await db.RelayLog.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task VacuumLogTablesAsync(CancellationToken ct = default)
    {
        // No engine returns space to the OS on DELETE alone, so the size-pressure trigger in PurgeWorker
        // only clears after an explicit reclaim - and each engine does it differently enough to belong in
        // the provider. Heavy locks on all of them; a maintenance action, never a hot-path call.
        await using var db = await contexts.CreateDbContextAsync(ct);
        await provider.ReclaimSpaceAsync(db, ["relay_log", "audit_log"], ct);
    }

    /// <summary>Column-named dictionary for the archive writer, which serialises whatever it is given.</summary>
    internal static IReadOnlyDictionary<string, object?> ToRow(RelayLogEntity r) => new Dictionary<string, object?>
    {
        ["id"] = r.Id,
        ["logged_at"] = r.LoggedAt,
        ["spool_id"] = r.SpoolId,
        ["event"] = r.Event,
        ["status"] = r.Status,
        ["retry_attempt"] = r.RetryAttempt,
        ["from_address"] = r.FromAddress,
        ["from_domain"] = r.FromDomain,
        ["to_addresses"] = r.ToAddresses,
        ["to_domain"] = r.ToDomain,
        ["subject"] = r.Subject,
        ["size_bytes"] = r.SizeBytes,
        ["relay_id"] = r.RelayId,
        ["relay_name"] = r.RelayName,
        ["routing_rule_id"] = r.RoutingRuleId,
        ["routing_rule_name"] = r.RoutingRuleName,
        ["routing_matched"] = r.RoutingMatched,
        ["provider"] = r.Provider,
        ["provider_message_id"] = r.ProviderMessageId,
        ["provider_response"] = r.ProviderResponse,
        ["duration_ms"] = r.DurationMs,
        ["error"] = r.Error,
        ["ingest_source"] = r.IngestSource,
        ["source_ip"] = r.SourceIp,
        ["api_key_id"] = r.ApiKeyId,
        ["api_key_name"] = r.ApiKeyName,
        ["tags"] = r.Tags,
        ["x_mailer"] = r.XMailer,
        ["attachment_count"] = r.AttachmentCount,
    };
}
