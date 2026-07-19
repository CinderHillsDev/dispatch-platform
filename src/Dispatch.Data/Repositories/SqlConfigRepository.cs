using Dapper;
using Dispatch.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Repositories;

/// <summary>Key/value config store with transparent encryption for sensitive rows (spec §12.3, §19.5).</summary>
public sealed class SqlConfigRepository(SqlConnectionFactory factory, ILogger<SqlConfigRepository>? logger = null) : IConfigRepository
{
    private readonly ILogger _log = logger ?? NullLogger<SqlConfigRepository>.Instance;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<(string Value, bool Encrypted)?>(
            new CommandDefinition("SELECT value AS Value, encrypted AS Encrypted FROM config WHERE \"key\" = @key",
                new { key }, cancellationToken: ct));
        if (row is null) return null;
        return row.Value.Encrypted ? TryDecrypt(key, row.Value.Value) : row.Value.Value;
    }

    public async Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
    {
        var stored = encrypted ? SecureConfig.Encrypt(value) : value;
        const string sql = """
            INSERT INTO config ("key", value, encrypted) VALUES (@key, @stored, @encrypted)
            ON CONFLICT ("key") DO UPDATE SET value = @stored, encrypted = @encrypted, updated_at = CURRENT_TIMESTAMP;
            """;
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(sql, new { key, stored, encrypted }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<(string Key, string Value, bool Encrypted, DateTime UpdatedAt)>(
            new CommandDefinition("SELECT \"key\" AS \"Key\", value AS Value, encrypted AS Encrypted, updated_at AS UpdatedAt FROM config",
                cancellationToken: ct));
        return rows
            .Select(r => new ConfigEntry(r.Key, r.Encrypted ? TryDecrypt(r.Key, r.Value) ?? "" : r.Value, r.Encrypted, r.UpdatedAt))
            .ToList();
    }

    // A machine-key change (or corrupt value) shouldn't crash config reads - log and treat as unset.
    private string? TryDecrypt(string key, string ciphertext)
    {
        try
        {
            return SecureConfig.Decrypt(ciphertext);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to decrypt config value for {Key} (machine key changed?); treating as unset", key);
            return null;
        }
    }
}
