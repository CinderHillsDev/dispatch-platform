using System.Security.Cryptography;
using Dapper;
using Dispatch.Core.ApiKeys;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Manages API keys (spec §7.6–§7.7, §17.4): generates <c>dsp_live_…</c> keys with 256 bits of entropy,
/// stores only their bcrypt (cost 12) hash, and verifies in constant time to avoid a timing oracle.
/// </summary>
public sealed class SqlApiKeyRepository(SqlConnectionFactory factory) : IApiKeyRepository
{
    private const string Prefix = "dsp_live_";
    private const int KeyIdLength = 12;
    private const int WorkFactor = 12;

    // A real bcrypt hash compared against when the key_id is unknown, so the timing matches the found path.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy", WorkFactor);

    private const string SelectColumns = """
        id, key_id AS KeyId, key_hash AS KeyHash, name, created_at AS CreatedAt, last_used_at AS LastUsedAt,
        message_count AS MessageCount, revoked, revoked_at AS RevokedAt, rate_limit_per_minute AS RateLimitPerMinute
        """;

    public async Task<ApiKeyCreated> CreateAsync(string name, int rateLimitPerMinute, CancellationToken ct = default)
    {
        var random = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plaintext = Prefix + random;
        var keyId = plaintext[..KeyIdLength];
        var hash = BCrypt.Net.BCrypt.HashPassword(plaintext, WorkFactor);

        const string insert = """
            INSERT INTO api_keys (key_id, key_hash, name, rate_limit_per_minute)
            OUTPUT INSERTED.id, INSERTED.key_id AS KeyId, INSERTED.key_hash AS KeyHash, INSERTED.name,
                   INSERTED.created_at AS CreatedAt, INSERTED.last_used_at AS LastUsedAt,
                   INSERTED.message_count AS MessageCount, INSERTED.revoked, INSERTED.revoked_at AS RevokedAt,
                   INSERTED.rate_limit_per_minute AS RateLimitPerMinute
            VALUES (@keyId, @hash, @name, @rateLimitPerMinute);
            """;

        await using var cn = await factory.OpenAsync(ct);
        var inserted = await cn.QuerySingleAsync<Row>(new CommandDefinition(
            insert, new { keyId, hash, name, rateLimitPerMinute }, cancellationToken: ct));

        return new ApiKeyCreated(inserted.ToApiKey(), plaintext);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        var where = includeRevoked ? "" : "WHERE revoked = 0";
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM api_keys {where} ORDER BY created_at DESC", cancellationToken: ct));
        return rows.Select(r => r.ToApiKey()).ToList();
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var affected = await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE api_keys SET revoked = 1, revoked_at = SYSUTCDATETIME() WHERE id = @id AND revoked = 0",
            new { id }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<ApiKey?> VerifyAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawKey) || rawKey.Length < KeyIdLength || !rawKey.StartsWith(Prefix))
        {
            BCrypt.Net.BCrypt.Verify("dummy", DummyHash);   // constant-time even on malformed input
            return null;
        }

        var keyId = rawKey[..KeyIdLength];
        await using var cn = await factory.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM api_keys WHERE key_id = @keyId AND revoked = 0",
            new { keyId }, cancellationToken: ct));

        if (row is null)
        {
            BCrypt.Net.BCrypt.Verify(rawKey, DummyHash);    // prevent timing-based key-id enumeration
            return null;
        }

        return BCrypt.Net.BCrypt.Verify(rawKey, row.KeyHash) ? row.ToApiKey() : null;
    }

    public async Task RecordUsageAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE api_keys SET last_used_at = SYSUTCDATETIME(), message_count = message_count + 1 WHERE id = @id",
            new { id }, cancellationToken: ct));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Row
    {
        public int Id { get; init; }
        public string KeyId { get; init; } = "";
        public string KeyHash { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? LastUsedAt { get; init; }
        public long MessageCount { get; init; }
        public bool Revoked { get; init; }
        public DateTime? RevokedAt { get; init; }
        public int RateLimitPerMinute { get; init; }

        public ApiKey ToApiKey() => new()
        {
            Id = Id, KeyId = KeyId, KeyHash = KeyHash, Name = Name, CreatedAt = CreatedAt,
            LastUsedAt = LastUsedAt, MessageCount = MessageCount, Revoked = Revoked,
            RevokedAt = RevokedAt, RateLimitPerMinute = RateLimitPerMinute,
        };
    }
}
