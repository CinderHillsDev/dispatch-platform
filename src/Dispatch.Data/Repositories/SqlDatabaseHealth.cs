using Dispatch.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>
/// Probes the database so /health reports "unreachable" rather than hanging.
///
/// The budget is enforced with a linked cancellation token rather than a connection-string timeout: each
/// engine spells that keyword differently (Timeout, Connect Timeout, Connection Timeout), and a health
/// check that has to know which engine it is talking to defeats the point of the provider abstraction.
/// </summary>
public sealed class SqlDatabaseHealth(IDbContextFactory<DispatchDbContext> contexts) : IDatabaseHealth
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cts.Token);
            return await db.Database.CanConnectAsync(cts.Token);
        }
        catch
        {
            // Includes the timeout: an unreachable database is a health result, never an exception.
            return false;
        }
    }
}
