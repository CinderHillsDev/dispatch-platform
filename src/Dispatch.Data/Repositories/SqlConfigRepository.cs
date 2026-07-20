using Dispatch.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Repositories;

/// <summary>Key/value config store with transparent encryption for sensitive rows (spec §12.3, §19.5).</summary>
public sealed class SqlConfigRepository(
    IDbContextFactory<DispatchDbContext> contexts,
    ILogger<SqlConfigRepository>? logger = null) : IConfigRepository
{
    private readonly ILogger _log = logger ?? NullLogger<SqlConfigRepository>.Instance;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Config.AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => new { c.Value, c.Encrypted })
            .SingleOrDefaultAsync(ct);

        if (row is null) return null;
        return row.Encrypted ? TryDecrypt(key, row.Value) : row.Value;
    }

    /// <summary>
    /// Upsert by key. Written as read-then-write rather than a provider-specific UPSERT because config
    /// writes are administrative and uncontended - unlike the relay counters, where concurrent workers make
    /// a single atomic statement mandatory (see IDatabaseProvider.CounterUpsertSql).
    /// </summary>
    public async Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default)
    {
        var stored = encrypted ? SecureConfig.Encrypt(value) : value;

        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.Config.SingleOrDefaultAsync(c => c.Key == key, ct);
        if (existing is null)
        {
            db.Config.Add(new ConfigEntity
            {
                Key = key,
                Value = stored,
                Encrypted = encrypted,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Value = stored;
            existing.Encrypted = encrypted;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Config.AsNoTracking().ToListAsync(ct);

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
