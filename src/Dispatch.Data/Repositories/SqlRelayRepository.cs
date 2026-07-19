using Dapper;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Reads/writes the <c>relays</c> table with a short TTL cache on the default relay so the dispatch hot
/// path avoids a SQL query per message (spec §10.2, §19.7). Writes invalidate the cache.
/// </summary>
public sealed class SqlRelayRepository(SqlConnectionFactory factory) : IRelayRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private const string SelectColumns =
        "id, name, provider, is_default AS IsDefault, enabled, max_concurrency AS MaxConcurrency, max_message_bytes AS MaxMessageBytes";
    private const string InsertedColumns =
        "id, name, provider, is_default AS \"IsDefault\", enabled, max_concurrency AS \"MaxConcurrency\", max_message_bytes AS \"MaxMessageBytes\"";

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

        await using var cn = await factory.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM relays WHERE is_default AND enabled LIMIT 1", cancellationToken: ct));

        var record = row?.ToRecord();
        lock (_lock) { _cachedDefault = record; _cachedAtUtc = DateTime.UtcNow; }
        return record;
    }

    public async Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM relays ORDER BY id", cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM relays WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToRecord();
    }

    public async Task<RelayRecord> CreateAsync(
        string name, RelayProviderType provider, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default)
    {
        // The first relay created becomes the catch-all automatically (no pre-seeded placeholder), so a
        // single-provider setup "just works" with no extra step. Subsequent relays are non-default.
        const string sql = $"""
            INSERT INTO relays (name, provider, max_concurrency, max_message_bytes, is_default)
            VALUES (@name, @provider, @maxConcurrency, @maxMessageBytes,
                    CASE WHEN EXISTS (SELECT 1 FROM relays WHERE is_default) THEN false ELSE true END)
            RETURNING {InsertedColumns};
            """;
        await using var cn = await factory.OpenAsync(ct);
        var row = await cn.QuerySingleAsync<Row>(new CommandDefinition(
            sql, new { name, provider = provider.ToString(), maxConcurrency, maxMessageBytes }, cancellationToken: ct));
        InvalidateCache();
        return row.ToRecord();
    }

    public async Task<bool> UpdateAsync(
        int id, string name, RelayProviderType provider, bool enabled, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE relays SET name = @name, provider = @provider, enabled = @enabled, max_concurrency = @maxConcurrency,
                              max_message_bytes = @maxMessageBytes, updated_at = CURRENT_TIMESTAMP
            WHERE id = @id;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(
            sql, new { id, name, provider = provider.ToString(), enabled, maxConcurrency, maxMessageBytes }, cancellationToken: ct));
        InvalidateCache();
        return n > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            // Preserve log history (relay_name is denormalised) but clear the FK; drop counters + credentials.
            await cn.ExecuteAsync(new CommandDefinition("UPDATE relay_log SET relay_id = NULL WHERE relay_id = @id", new { id }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("DELETE FROM relay_counters WHERE relay_id = @id", new { id }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("DELETE FROM config WHERE \"key\" LIKE @prefix", new { prefix = $"relay:{id}:%" }, tx, cancellationToken: ct));
            var n = await cn.ExecuteAsync(new CommandDefinition("DELETE FROM relays WHERE id = @id AND NOT is_default", new { id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            InvalidateCache();
            return n > 0;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> SetDefaultAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            var exists = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM relays WHERE id = @id", new { id }, tx, cancellationToken: ct));
            if (exists is null) { await tx.RollbackAsync(ct); return false; }

            await cn.ExecuteAsync(new CommandDefinition("UPDATE relays SET is_default = false WHERE is_default", transaction: tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("UPDATE relays SET is_default = true, enabled = true WHERE id = @id", new { id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            InvalidateCache();
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private void InvalidateCache()
    {
        lock (_lock) { _cachedDefault = null; _cachedAtUtc = DateTime.MinValue; }
    }

    private sealed class Row
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Provider { get; init; } = "";
        public bool IsDefault { get; init; }
        public bool Enabled { get; init; }
        public int MaxConcurrency { get; init; }
        public long MaxMessageBytes { get; init; }

        public RelayRecord ToRecord() => new()
        {
            Id = Id,
            Name = Name,
            Provider = Enum.TryParse<RelayProviderType>(Provider, ignoreCase: true, out var p) ? p : RelayProviderType.Unconfigured,
            IsDefault = IsDefault,
            Enabled = Enabled,
            MaxConcurrency = MaxConcurrency,
            MaxMessageBytes = MaxMessageBytes,
        };
    }
}
