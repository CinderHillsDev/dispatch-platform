using Dapper;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Reads the <c>relays</c> table with a short TTL cache so rule/relay changes propagate quickly without
/// a SQL query per message (spec §19.7).
/// </summary>
public sealed class SqlRelayRepository(SqlConnectionFactory factory) : IRelayRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private const string SelectColumns =
        "id, name, provider, is_default AS IsDefault, enabled, max_concurrency AS MaxConcurrency, max_message_bytes AS MaxMessageBytes";

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
            $"SELECT TOP 1 {SelectColumns} FROM relays WHERE is_default = 1 AND enabled = 1",
            cancellationToken: ct));

        var record = row?.ToRecord();
        lock (_lock)
        {
            _cachedDefault = record;
            _cachedAtUtc = DateTime.UtcNow;
        }
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
            Provider = Enum.TryParse<RelayProviderType>(Provider, ignoreCase: true, out var p) ? p : RelayProviderType.None,
            IsDefault = IsDefault,
            Enabled = Enabled,
            MaxConcurrency = MaxConcurrency,
            MaxMessageBytes = MaxMessageBytes,
        };
    }
}
