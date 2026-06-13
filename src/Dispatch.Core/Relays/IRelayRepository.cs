namespace Dispatch.Core.Relays;

/// <summary>Reads named relay configurations from SQL (spec §6.11). Implementations cache with a short TTL (§19.7).</summary>
public interface IRelayRepository
{
    Task<RelayRecord?> GetDefaultAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default);
    Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default);
}
