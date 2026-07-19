using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data;

/// <summary>
/// Builds <see cref="DispatchDbContext"/> instances for a given engine.
///
/// Contexts are created per-operation from an <c>IDbContextFactory</c> rather than injected as a scoped
/// dependency, because the repositories are singletons called concurrently from SpoolWorkerPool's worker
/// threads and a DbContext is not thread-safe.
/// </summary>
public static class DispatchDbContextFactory
{
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString)
    {
        DatabaseProviders.Get(provider).Configure(builder, connectionString);
        return builder;
    }

    /// <summary>
    /// A standalone factory for use outside dependency injection — tests, the migrator, and tooling.
    /// Application code takes its factory from DI (AddDispatchData), which shares one configured instance.
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
}
