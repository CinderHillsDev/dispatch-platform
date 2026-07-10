namespace Dispatch.Core.Configuration;

/// <summary>
/// Key/value application settings stored in the SQL <c>config</c> table (spec §12.3).
/// Values flagged encrypted are transparently AES-encrypted at rest (§19.5).
/// </summary>
public interface IConfigRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default);

    /// <summary>All config rows, decrypted. Callers must redact encrypted values before returning over HTTP (§17.5).</summary>
    Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default);
}

public sealed record ConfigEntry(string Key, string Value, bool Encrypted, DateTime UpdatedAt);
