using Dispatch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Data.Tests;

/// <summary>
/// Guards the wiring the repositories depend on, so it cannot silently drift from what the benchmarks and
/// tests exercise.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void The_context_factory_is_pooled()
    {
        // The ingest path takes a fresh context per operation from many threads. It must be POOLED, and DI
        // must register the SAME kind the tests and migrator use (DispatchDbContextFactory.Create). This
        // once diverged - DI registered the non-pooled AddDbContextFactory while ConcurrentWriteTests ran
        // pooled - so the measured throughput was one production never saw. Assert the type directly.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDispatchData("Data Source=:memory:");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<DispatchDbContext>>();

        Assert.IsType<PooledDbContextFactory<DispatchDbContext>>(factory);
    }
}
