using System.Security.Cryptography;
using Dispatch.Core.ApiKeys;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Manages API keys (spec §7.6-§7.7, §17.4): generates <c>dsp_live_…</c> keys with 256 bits of entropy,
/// stores only their bcrypt (cost 12) hash, and verifies in constant time to avoid a timing oracle.
/// </summary>
public sealed class SqlApiKeyRepository(IDbContextFactory<DispatchDbContext> contexts) : IApiKeyRepository
{
    private const string Prefix = "dsp_live_";
    private const int KeyIdLength = 12;
    private const int WorkFactor = 12;

    // A real bcrypt hash compared against when the key_id is unknown, so the timing matches the found path.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy", WorkFactor);

    public async Task<ApiKeyCreated> CreateAsync(string name, int rateLimitPerMinute, CancellationToken ct = default)
    {
        var random = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plaintext = Prefix + random;

        var entity = new ApiKeyEntity
        {
            KeyId = plaintext[..KeyIdLength],
            KeyHash = BCrypt.Net.BCrypt.HashPassword(plaintext, WorkFactor),
            Name = name,
            CreatedAt = DateTime.UtcNow,
            RateLimitPerMinute = rateLimitPerMinute,
        };

        await using var db = await contexts.CreateDbContextAsync(ct);
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);   // populates the generated id

        // The plaintext is returned exactly once, here. Only its hash is stored, so it cannot be recovered.
        return new ApiKeyCreated(ToApiKey(entity), plaintext);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.ApiKeys.AsNoTracking();
        if (!includeRevoked) query = query.Where(k => !k.Revoked);

        var rows = await query.OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return rows.Select(ToApiKey).ToList();
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Guarded on NOT revoked so a second revoke reports false rather than resetting revoked_at.
        return await db.ApiKeys
            .Where(k => k.Id == id && !k.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.Revoked, true)
                .SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct) > 0;
    }

    public async Task<ApiKey?> VerifyAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawKey) || rawKey.Length < KeyIdLength || !rawKey.StartsWith(Prefix))
        {
            // Verify the input against a real hash, not a constant, so malformed keys cost the same as
            // well-formed ones and cannot be distinguished by timing.
            BCrypt.Net.BCrypt.Verify(rawKey ?? "", DummyHash);
            return null;
        }

        var keyId = rawKey[..KeyIdLength];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ApiKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.KeyId == keyId && !k.Revoked, ct);

        if (row is null)
        {
            BCrypt.Net.BCrypt.Verify(rawKey, DummyHash);    // prevent timing-based key-id enumeration
            return null;
        }

        return BCrypt.Net.BCrypt.Verify(rawKey, row.KeyHash) ? ToApiKey(row) : null;
    }

    /// <summary>
    /// Bumps usage counters. Written as a set-based update rather than load-modify-save: it runs on every
    /// authenticated API request, and a read-then-write would lose counts when a key is used concurrently.
    /// </summary>
    public async Task RecordUsageAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.ApiKeys
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.LastUsedAt, DateTime.UtcNow)
                .SetProperty(k => k.MessageCount, k => k.MessageCount + 1), ct);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ApiKey ToApiKey(ApiKeyEntity e) => new()
    {
        Id = e.Id, KeyId = e.KeyId, KeyHash = e.KeyHash, Name = e.Name, CreatedAt = e.CreatedAt,
        LastUsedAt = e.LastUsedAt, MessageCount = e.MessageCount, Revoked = e.Revoked,
        RevokedAt = e.RevokedAt, RateLimitPerMinute = e.RateLimitPerMinute,
    };
}
