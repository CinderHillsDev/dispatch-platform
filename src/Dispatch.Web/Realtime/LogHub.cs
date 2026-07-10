using Microsoft.AspNetCore.SignalR;

namespace Dispatch.Web.Realtime;

/// <summary>SignalR hub for the live activity feed. On connect, replays the recent-events ring (spec §9.2).</summary>
public sealed class LogHub(RelayEventStream stream) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("recent", stream.Recent);
        await base.OnConnectedAsync();
    }
}
