using Dispatch.Core.Providers;

namespace Dispatch.Core.Relays;

/// <summary>Reads/writes named relay configurations (spec §6.11, §10.2). Read paths cache with a short TTL (§19.7).</summary>
public interface IRelayRepository
{
    Task<RelayRecord?> GetDefaultAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default);
    Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<RelayRecord> CreateAsync(string name, RelayProviderType provider, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default);
    Task<bool> UpdateAsync(int id, string name, RelayProviderType provider, bool enabled, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default);
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Makes the given relay the default, demoting the current one atomically (spec §10.2).</summary>
    Task<bool> SetDefaultAsync(int id, CancellationToken ct = default);
}
