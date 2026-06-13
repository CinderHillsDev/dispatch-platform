namespace Dispatch.Core.ApiKeys;

/// <summary>Manages HTTP ingestion API keys (spec §7.6–§7.7, §17.4).</summary>
public interface IApiKeyRepository
{
    /// <summary>Generates a <c>dsp_live_…</c> key (256-bit), stores only its bcrypt hash, returns the plaintext once.</summary>
    Task<ApiKeyCreated> CreateAsync(string name, int rateLimitPerMinute, CancellationToken ct = default);

    Task<IReadOnlyList<ApiKey>> ListAsync(bool includeRevoked = false, CancellationToken ct = default);

    Task<bool> RevokeAsync(int id, CancellationToken ct = default);

    /// <summary>Looks up by key_id prefix then bcrypt-verifies. Constant-time even when not found (§17.4).</summary>
    Task<ApiKey?> VerifyAsync(string rawKey, CancellationToken ct = default);

    Task RecordUsageAsync(int id, CancellationToken ct = default);
}
