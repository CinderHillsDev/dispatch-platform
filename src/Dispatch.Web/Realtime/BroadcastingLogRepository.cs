using Dispatch.Core.Logging;

namespace Dispatch.Web.Realtime;

/// <summary>
/// Decorates the real <see cref="ILogRepository"/>: after a row is persisted, pushes a compact event to
/// the live dashboard feed. Keeps SignalR concerns out of the worker and the SQL repo.
/// </summary>
public sealed class BroadcastingLogRepository(ILogRepository inner, RelayEventStream stream) : ILogRepository
{
    public async Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default)
    {
        await inner.InsertAsync(entry, ct);
        await stream.PublishAsync(RelayEventDto.From(entry), ct);
    }
}
