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
    /// POOLED, matching what DI registers (AddDispatchData). Every repository call creates a context, and on
    /// the ingest path that happens several times per message from many threads at once; pooling reuses the
    /// instances and resets their state instead of re-running the context's internal service resolution.
    /// The measured gain is modest - roughly 1.1x-1.5x on SQLite (ProductionPoolingBenchmarkTests) - and is
    /// NOT the 3.5x the WAL synchronous pragma delivers; see the note in ServiceCollectionExtensions. The
    /// reason this path must also be pooled is correctness of measurement, not the speed-up: the tests and
    /// production must exercise the same shape.
    /// </summary>
    public static IDbContextFactory<DispatchDbContext> Create(DatabaseProvider provider, string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DispatchDbContext>();
        Configure(builder, provider, connectionString);
        return new PooledDbContextFactory<DispatchDbContext>(builder.Options);
    }
}
