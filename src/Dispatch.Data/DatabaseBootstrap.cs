using Dispatch.Data.Providers;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Creates the database if it does not exist and waits for a server that is still starting - the work that
/// has to happen before EF can connect. Delegates to the engine's provider; see
/// <see cref="IDatabaseProvider.EnsureDatabaseAsync"/>.
/// </summary>
public sealed class DatabaseBootstrap(
    DatabaseProvider provider, string connectionString, ILogger<DatabaseBootstrap>? log = null)
{
    public Task EnsureDatabaseAsync(CancellationToken ct = default) =>
        DatabaseProviders.Get(provider).EnsureDatabaseAsync(connectionString, log, ct);
}
