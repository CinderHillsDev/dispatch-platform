using Microsoft.AspNetCore.SignalR;

namespace Dispatch.Web.Realtime;

/// <summary>
/// SignalR hub for the live provider-test log (spec §11). Each test run is its own group (named by the
/// run id) so concurrent tests from different browser tabs do not cross-contaminate. Clients call
/// <see cref="Join"/> with the run id they received from <c>POST /api/config/test-provider</c>.
/// </summary>
public sealed class TestProviderHub : Hub
{
    public Task Join(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);
}
