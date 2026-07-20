using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dispatch.Data.Providers;

/// <summary>
/// Runs the provider's per-connection setup on every connection EF opens.
///
/// This exists because per-connection state is exactly that: SQLite's <c>synchronous</c> pragma, unlike
/// <c>journal_mode</c>, is NOT stored in the database file. Setting it once at bootstrap configured a
/// throwaway connection and nothing else, so every connection the application actually used silently ran
/// with the default <c>synchronous=FULL</c> - an fsync on every commit, which capped concurrent writes at
/// roughly a thousand a second.
///
/// EF owns connection lifetime, so there is no other place to hook this. Without the interceptor,
/// <see cref="IDatabaseProvider.OnConnectionOpenedAsync"/> is dead code that looks like a guarantee.
/// </summary>
public sealed class ProviderConnectionInterceptor(IDatabaseProvider provider) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await provider.OnConnectionOpenedAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        // EF opens connections synchronously on some paths (migrations, and any sync API call). Blocking
        // here is acceptable: these are PRAGMA statements against an already-open connection, and skipping
        // them would leave that connection without the settings the rest of the system assumes.
        provider.OnConnectionOpenedAsync(connection).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }
}
