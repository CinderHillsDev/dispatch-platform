namespace Dispatch.Core.ApiKeys;

/// <summary>A row from the SQL <c>api_keys</c> table (spec §7.6). Never carries the plaintext key.</summary>
public sealed class ApiKey
{
    public int Id { get; init; }
    public string KeyId { get; init; } = "";          // public prefix, e.g. dsp_live_a1b
    public string KeyHash { get; init; } = "";         // bcrypt hash of the full key
    public string Name { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public long MessageCount { get; init; }
    public bool Revoked { get; init; }
    public DateTime? RevokedAt { get; init; }
    public int RateLimitPerMinute { get; init; }
}

/// <summary>Returned once on creation — the only time the plaintext key is available (spec §7.6).</summary>
public sealed record ApiKeyCreated(ApiKey Key, string PlaintextKey);
