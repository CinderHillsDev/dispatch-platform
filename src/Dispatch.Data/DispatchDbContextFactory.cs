using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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
        var engine = DatabaseProviders.Get(provider);
        engine.Configure(builder, connectionString);
        // Per-connection session state has to be applied to the connections EF actually opens, not just at
        // bootstrap - see ProviderConnectionInterceptor.
        builder.AddInterceptors(new ProviderConnectionInterceptor(engine));
        return builder;
    }

    /// <summary>
    /// A standalone factory for use outside dependency injection - tests, the migrator, and tooling.
    /// Application code takes its factory from DI (AddDispatchData), which shares one configured instance.
    ///
    /// POOLED, matching what DI registers. Every repository call creates a context, and on the ingest path
    /// that happens several times per message from many threads at once; constructing one per call means
    /// re-running the context's internal service resolution each time. Pooling reuses the instances and
    /// resets their state instead, which is the difference between about 1,000 and several thousand
    /// concurrent writes per second (see ConcurrentWriteTests).
    /// </summary>
    public static IDbContextFactory<DispatchDbContext> Create(DatabaseProvider provider, string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DispatchDbContext>();
        Configure(builder, provider, connectionString);
        return new PooledDbContextFactory<DispatchDbContext>(builder.Options);
    }
}
