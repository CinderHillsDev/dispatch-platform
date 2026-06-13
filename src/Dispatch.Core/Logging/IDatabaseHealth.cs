namespace Dispatch.Core.Logging;

/// <summary>Fast, non-throwing database reachability probe for the /health endpoint.</summary>
public interface IDatabaseHealth
{
    /// <summary>Returns true if the database answers a trivial query within a short budget; never throws.</summary>
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}
