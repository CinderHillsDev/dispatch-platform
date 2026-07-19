using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Brings the database up to the current schema on every startup. Idempotent: safe against a brand-new
/// database and an already-current one alike.
///
/// There is deliberately no handling for databases created before EF migrations existed. Every install is
/// a new install, so that path was dead code guarding against a situation that cannot arise - and dead
/// recovery code is worse than none, because it looks like a safety net nobody has tested.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<DispatchDbContext> contexts,
    DatabaseBootstrap bootstrap,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Create the database/file and wait for the server to accept connections. EF's MigrateAsync can
        // create a database, but it will not wait for one that is still starting - which is exactly what a
        // compose stack or a freshly-provisioned VM does on first boot.
        await bootstrap.EnsureDatabaseAsync(ct);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var provider = db.Database.ProviderName;

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            log.LogInformation("Database schema is current. [{Provider}]", provider);
            return;
        }

        log.LogInformation("Applying {Count} migration(s): {Migrations} [{Provider}]",
            pending.Count, string.Join(", ", pending), provider);
        await db.Database.MigrateAsync(ct);
    }
}
