using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Dispatch.Web.Realtime;

/// <summary>
/// Holds the last N relay events for replay-on-connect and broadcasts each new event to connected
/// SignalR clients (spec §9.2, §19.6). Singleton - shared by the broadcasting log repository and the hub.
/// </summary>
public sealed class RelayEventStream(IHubContext<LogHub> hub)
{
    private const int Capacity = 50;
    private readonly ConcurrentQueue<RelayEventDto> _recent = new();

    public IReadOnlyList<RelayEventDto> Recent => _recent.ToArray();

    public async Task PublishAsync(RelayEventDto evt, CancellationToken ct = default)
    {
        _recent.Enqueue(evt);
        while (_recent.Count > Capacity)
            _recent.TryDequeue(out _);

        await hub.Clients.All.SendAsync("relayEvent", evt, ct);
    }
}
