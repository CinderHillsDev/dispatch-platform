using System.Diagnostics;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data;
using Dispatch.Data.Providers;
using Dispatch.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Data.Tests;

/// <summary>
/// Proves the pooling fix delivers on the path that actually ships, and quantifies it.
///
/// The review found that DI registered the non-pooled context factory while only the test fixture was
/// pooled - so the headline "~3,500 writes/sec" was measured on a path production did not use.
/// DependencyInjectionTests now asserts the resolved factory is the pooled TYPE, but a type assertion is
/// not a throughput measurement. This runs the ingest write pattern through the real
/// <see cref="ServiceCollectionExtensions.AddDispatchData"/> registration - the exact
/// <see cref="ILogRepository"/>/<see cref="ICounterRepository"/> the service resolves - and against a
/// deliberately non-pooled factory, so the delta the fix delivers is a number, not a claim.
///
/// Opt-in via DISPATCH_BENCH, because it is a timing measurement, not a pass/fail invariant. The assertion
/// is deliberately loose (pooled must not be dramatically SLOWER); the printed numbers are the point.
/// </summary>
public class ProductionPoolingBenchmarkTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("DISPATCH_BENCH") is { Length: > 0 } v
        && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Pooled_DI_path_matches_or_beats_the_non_pooled_path()
    {
        if (!Enabled) return;

        var pooledPath = Path.Combine(Path.GetTempPath(), $"bench_pooled_{Guid.NewGuid():N}.db");
        var plainPath = Path.Combine(Path.GetTempPath(), $"bench_plain_{Guid.NewGuid():N}.db");
        try
        {
            // The production path: build the container exactly as the service does.
            await using var pooled = BuildProductionProvider(pooledPath);
            var pooledRate = await MeasureAsync(pooled, "production DI (pooled)");

            // The path production used BEFORE the fix: same everything, non-pooled factory.
            await using var plain = BuildNonPooledProvider(plainPath);
            var plainRate = await MeasureAsync(plain, "non-pooled (pre-fix)");

            Console.WriteLine($"BENCH pooled/non-pooled ratio: {(double)pooledRate / plainRate:F2}x");

            // The fix must not have made the shipped path slower. It is expected to be faster; machine load
            // makes the exact multiple vary, so this only guards against a regression, not a target.
            Assert.True(pooledRate >= plainRate * 0.9,
                $"pooled DI path ({pooledRate}/s) is slower than non-pooled ({plainRate}/s) - the pooling fix regressed");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { pooledPath, plainPath })
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    File.Delete(p + suffix);
        }
    }

    /// <summary>The real registration the service uses (AddDispatchData → AddPooledDbContextFactory).</summary>
    private static ServiceProvider BuildProductionProvider(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDispatchData(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Identical wiring but with the NON-pooled factory, to stand in for the pre-fix production path. Built
    /// by hand rather than by flipping AddDispatchData, so the shipped code has exactly one registration.
    /// </summary>
    private static ServiceProvider BuildNonPooledProvider(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DatabaseBootstrap(DatabaseProvider.Sqlite, cs));
        services.AddSingleton(DatabaseProviders.Get(DatabaseProvider.Sqlite));
        services.AddDbContextFactory<DispatchDbContext>(o =>
            DispatchDbContextFactory.Configure(o, DatabaseProvider.Sqlite, cs));
        services.AddSingleton<SqlLogRepository>();
        services.AddSingleton<ILogRepository>(sp => sp.GetRequiredService<SqlLogRepository>());
        services.AddSingleton<SqlCounterRepository>();
        services.AddSingleton<ICounterRepository>(sp => sp.GetRequiredService<SqlCounterRepository>());
        return services.BuildServiceProvider();
    }

    private static async Task<int> MeasureAsync(IServiceProvider sp, string label)
    {
        // Create the schema through a context from the same factory.
        var contexts = sp.GetRequiredService<IDbContextFactory<DispatchDbContext>>();
        var bootstrap = sp.GetRequiredService<DatabaseBootstrap>();
        await new DatabaseInitializer(contexts, bootstrap,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var seed = await contexts.CreateDbContextAsync())
        {
            seed.Relays.Add(new RelayEntity { Name = "bench", Provider = "Local", Enabled = true });
            await seed.SaveChangesAsync();
        }
        int relayId;
        await using (var q = await contexts.CreateDbContextAsync())
            relayId = await q.Relays.Select(r => r.Id).SingleAsync();

        var log = sp.GetRequiredService<ILogRepository>();
        var counters = sp.GetRequiredService<ICounterRepository>();

        const int writers = 16, each = 25;
        var total = writers * each;

        // Warm once so the first-call JIT/model realization is not charged to the timed run.
        await log.InsertAsync(Sample("warm", relayId));

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < each; i++)
            {
                await log.InsertAsync(Sample($"{w:D2}-{i:D3}-{Guid.NewGuid():N}", relayId));
                await counters.IncrementAsync(relayId, CounterField.Received);
                await counters.IncrementAsync(relayId, CounterField.Delivered);
            }
        })));
        sw.Stop();

        var writes = total * 3;   // one insert + two counter upserts per message
        var rate = (int)(writes * 1000.0 / sw.ElapsedMilliseconds);
        Console.WriteLine($"BENCH {label,-24} {writes} writes in {sw.ElapsedMilliseconds,5}ms = {rate,6}/sec");
        return rate;
    }

    private static RelayLogEntry Sample(string spoolId, int relayId) => new()
    {
        Event = "Delivered", Status = "OK", SpoolId = spoolId,
        FromAddress = "sender@example.com", FromDomain = "example.com",
        ToAddresses = ["recipient@example.net"], ToDomain = "example.net",
        Subject = "benchmark", RelayId = relayId, RelayName = "bench", SizeBytes = 2048,
    };
}
