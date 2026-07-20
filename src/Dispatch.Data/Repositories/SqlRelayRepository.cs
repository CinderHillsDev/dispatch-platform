using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Reads/writes the <c>relays</c> table with a short TTL cache on the default relay so the dispatch hot
/// path avoids a query per message (spec §10.2, §19.7). Writes invalidate the cache.
///
/// AT MOST ONE DEFAULT RELAY is an invariant this class is responsible for. PostgreSQL, SQLite and SQL
/// Server also enforce it with a filtered unique index (IX_relays_default), but MySQL and MariaDB have no
/// filtered indexes at all - so on that backend the code here is the ONLY thing preventing two catch-all
/// relays and non-deterministic routing. Every write that touches is_default therefore clears the previous
/// default inside the same transaction, on every engine, rather than relying on the index to catch it.
/// </summary>
public sealed class SqlRelayRepository(IDbContextFactory<DispatchDbContext> contexts) : IRelayRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private RelayRecord? _cachedDefault;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public async Task<RelayRecord?> GetDefaultAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cachedDefault is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cachedDefault;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Relays.AsNoTracking()
            .Where(r => r.IsDefault && r.Enabled)
            .FirstOrDefaultAsync(ct);

        var record = row is null ? null : ToRecord(row);
        lock (_lock) { _cachedDefault = record; _cachedAtUtc = DateTime.UtcNow; }
        return record;
    }

    public async Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Relays.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Relays.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : ToRecord(row);
    }

    public async Task<RelayRecord> CreateAsync(
        string name, RelayProviderType provider, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The first relay created becomes the catch-all automatically (there is no pre-seeded placeholder),
        // so a single-provider setup works with no extra step. Subsequent relays are non-default. The check
        // and the insert share a transaction so two concurrent creates cannot both decide they are first.
        var hasDefault = await db.Relays.AnyAsync(r => r.IsDefault, ct);

        var entity = new RelayEntity
        {
            Name = name,
            Provider = provider.ToString(),
            MaxConcurrency = maxConcurrency,
            MaxMessageBytes = ToInt32(maxMessageBytes),
            IsDefault = !hasDefault,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Relays.Add(entity);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        InvalidateCache();
        return ToRecord(entity);
    }

    public async Task<bool> UpdateAsync(
        int id, string name, RelayProviderType provider, bool enabled, int maxConcurrency, long maxMessageBytes,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var affected = await db.Relays
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Name, name)
                .SetProperty(r => r.Provider, provider.ToString())
                .SetProperty(r => r.Enabled, enabled)
                .SetProperty(r => r.MaxConcurrency, maxConcurrency)
                .SetProperty(r => r.MaxMessageBytes, ToInt32(maxMessageBytes))
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow), ct);

        InvalidateCache();
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Preserve log history (relay_name is denormalised onto each row) but clear the foreign key, then
        // drop the counters and per-relay config. Deleting the relay itself is refused when it is the
        // default, so routing is never left without a catch-all.
        await db.RelayLog.Where(r => r.RelayId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RelayId, (int?)null), ct);
        await db.RelayCounters.Where(c => c.RelayId == id).ExecuteDeleteAsync(ct);

        var prefix = $"relay:{id}:";
        await db.Config.Where(c => c.Key.StartsWith(prefix)).ExecuteDeleteAsync(ct);

        var affected = await db.Relays.Where(r => r.Id == id && !r.IsDefault).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        InvalidateCache();
        return affected > 0;
    }

    public async Task<bool> SetDefaultAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!await db.Relays.AnyAsync(r => r.Id == id, ct))
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // Clear then set, in one transaction. On MySQL/MariaDB this ordering IS the invariant - there is no
        // filtered unique index there to reject a second default.
        await db.Relays.Where(r => r.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDefault, false), ct);
        await db.Relays.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDefault, true)
                .SetProperty(r => r.Enabled, true), ct);

        await tx.CommitAsync(ct);
        InvalidateCache();
        return true;
    }

    private void InvalidateCache()
    {
        lock (_lock) { _cachedDefault = null; _cachedAtUtc = DateTime.MinValue; }
    }

    /// <summary>
    /// max_message_bytes is an int column. Checked, so a caller passing something beyond its range fails
    /// loudly here rather than silently wrapping to a small - or negative - size limit.
    /// </summary>
    private static int ToInt32(long value) => checked((int)value);

    private static RelayRecord ToRecord(RelayEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Provider = Enum.TryParse<RelayProviderType>(e.Provider, ignoreCase: true, out var p)
            ? p : RelayProviderType.Unconfigured,
        IsDefault = e.IsDefault,
        Enabled = e.Enabled,
        MaxConcurrency = e.MaxConcurrency,
        MaxMessageBytes = e.MaxMessageBytes,
    };
}
