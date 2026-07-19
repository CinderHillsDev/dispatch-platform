using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data;

/// <summary>
/// Configures <see cref="DispatchDbContext"/> for whichever engine the connection string targets, and names
/// the matching migrations assembly.
///
/// Contexts are created per-operation from an <c>IDbContextFactory</c> rather than injected as a scoped
/// dependency, because the repositories are singletons called concurrently from SpoolWorkerPool's worker
/// threads and a DbContext is not thread-safe. That mirrors the open-a-connection-per-operation pattern the
/// Dapper repositories already used.
/// </summary>
public static class DispatchDbContextFactory
{
    /// <summary>Migrations live in a per-provider assembly; see the Dispatch.Data.* projects.</summary>
    public static string MigrationsAssembly(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "Dispatch.Data.Sqlite",
        DatabaseProvider.Postgres => "Dispatch.Data.Postgres",
        DatabaseProvider.SqlServer => "Dispatch.Data.SqlServer",
        DatabaseProvider.MySql => "Dispatch.Data.MySql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>
    /// Builds a standalone context factory for a connection string, outside dependency injection — used by
    /// tests, the migration bootstrap and tooling. Application code gets its factory from DI instead
    /// (AddDispatchData), which shares one configured instance.
    /// </summary>
    public static IDbContextFactory<DispatchDbContext> Create(DatabaseProvider provider, string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DispatchDbContext>();
        Configure(builder, provider, connectionString);
        return new OptionsFactory(builder.Options);
    }

    private sealed class OptionsFactory(DbContextOptions<DispatchDbContext> options)
        : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() => new(options);
    }

    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString)
    {
        var migrations = MigrationsAssembly(provider);

        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(connectionString, o => o.MigrationsAssembly(migrations));
                break;

            case DatabaseProvider.Postgres:
                builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(migrations));
                break;

            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(connectionString, o => o.MigrationsAssembly(migrations));
                break;

            case DatabaseProvider.MySql:
                // AutoDetect opens a connection to read the server banner, which distinguishes MariaDB from
                // MySQL — they diverge enough (and Pomelo generates different SQL for each) that assuming
                // one would produce subtly wrong DDL against the other.
                builder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    o => o.MigrationsAssembly(migrations));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }

        return builder;
    }
}
