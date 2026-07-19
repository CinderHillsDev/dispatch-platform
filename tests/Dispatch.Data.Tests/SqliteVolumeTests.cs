using System.Diagnostics;
using Dispatch.Core.Logging;
using Dispatch.Data.Providers;
using Dispatch.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// Volume characterisation for the SQLite backend, which is the default deployment and therefore the one
/// most installations will actually run.
///
/// The concurrency question is answered elsewhere (ConcurrentWriteTests: do parallel writers serialise
/// gracefully). This asks the different question that only shows up at size: once relay_log holds millions
/// of rows, does the Message Log still answer quickly, does ingest still keep up, and does purging still
/// complete - or does the default backend quietly fall over at the volume a busy relay reaches in a month?
///
/// Opt-in via DISPATCH_VOLUME_TEST, because it writes hundreds of thousands of rows and takes minutes.
/// Set DISPATCH_VOLUME_ROWS to change the target row count (default 250,000).
///
/// This is a characterisation test, not a pass/fail gate: the assertions are deliberately loose (orders of
/// magnitude, not milliseconds) so it fails on a collapse rather than on a slow CI agent. The numbers it
/// prints are the point - they are what tells you which volume the default stops being appropriate at.
/// </summary>
public class SqliteVolumeTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("DISPATCH_VOLUME_TEST") is { Length: > 0 } v
        && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    private static int TargetRows =>
        int.TryParse(Environment.GetEnvironmentVariable("DISPATCH_VOLUME_ROWS"), out var n) ? n : 250_000;

    [Fact]
    public async Task Message_log_stays_responsive_at_volume()
    {
        if (!Enabled) return;

        var path = Path.Combine(Path.GetTempPath(), $"dispatchvol_{Guid.NewGuid():N}.db");
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        try
        {
            var contexts = DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, cs);
            await new DatabaseInitializer(
                    contexts,
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, cs, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            var rows = TargetRows;
            Console.WriteLine($"VOLUME target={rows:N0} rows  file={path}");

            // ---- Bulk load -------------------------------------------------------------------------
            // Batched in transactions of 5,000. Per-row autocommit would measure fsync latency rather than
            // anything about SQLite's capacity, and the real ingest path commits per message anyway --
            // that rate is what ConcurrentWriteTests measures.
            var sw = Stopwatch.StartNew();
            await BulkLoadAsync(contexts, rows);
            sw.Stop();
            var loadRate = rows * 1000.0 / sw.ElapsedMilliseconds;
            Console.WriteLine($"VOLUME load    {rows:N0} rows in {sw.ElapsedMilliseconds:N0}ms ({loadRate:N0} rows/sec)");

            var fileBytes = new FileInfo(path).Length;
            Console.WriteLine($"VOLUME size    {fileBytes / 1024.0 / 1024.0:N1} MiB total, {(double)fileBytes / rows:N0} bytes/row");

            var query = new SqlMessageLogQuery(contexts);

            // ---- First page: the dashboard's default view, newest-first, no filter ------------------
            sw.Restart();
            var firstPage = await query.QueryAsync(new MessageLogFilter { Limit = 50 });
            sw.Stop();
            var firstPageMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"VOLUME page1   {firstPage.Rows.Count} rows in {firstPageMs}ms");
            Assert.NotEmpty(firstPage.Rows);

            // ---- Deep pagination: the keyset cursor's whole purpose is that page 100 costs the same as
            // page 1. If it degrades, the cursor has silently fallen back to a scan.
            var cursor = firstPage.NextCursor;
            var deepMs = 0L;
            for (var i = 0; i < 20 && cursor is not null; i++)
            {
                sw.Restart();
                var page = await query.QueryAsync(new MessageLogFilter { Limit = 50, Cursor = cursor });
                sw.Stop();
                deepMs = sw.ElapsedMilliseconds;
                cursor = page.NextCursor;
            }
            Console.WriteLine($"VOLUME page20  {deepMs}ms (keyset cursor: should track page1, not row count)");

            // ---- Indexed filter --------------------------------------------------------------------
            sw.Restart();
            var byDomain = await query.QueryAsync(new MessageLogFilter { ToDomain = "bulk-7.example.net", Limit = 50 });
            sw.Stop();
            Console.WriteLine($"VOLUME filter  to_domain -> {byDomain.Rows.Count} rows in {sw.ElapsedMilliseconds}ms");

            // ---- Unindexed filter: leading-wildcard subject LIKE. Known non-sargable (see migration
            // 0005) and expected to scan; measured so the cost of the known-bad path is on record.
            sw.Restart();
            var bySubject = await query.QueryAsync(new MessageLogFilter { Subject = "needle-42", Limit = 50 });
            sw.Stop();
            Console.WriteLine($"VOLUME scan    subject LIKE -> {bySubject.Rows.Count} rows in {sw.ElapsedMilliseconds}ms (unindexed, expected)");

            // ---- Aggregates behind the dashboard counters ------------------------------------------
            sw.Restart();
            await using (var probe = await contexts.CreateDbContextAsync())
                await probe.RelayLog.LongCountAsync();
            sw.Stop();
            Console.WriteLine($"VOLUME count   full count(*) in {sw.ElapsedMilliseconds}ms");

            // ---- Purge, the operation that has to survive a backlog ---------------------------------
            var maintenance = new SqlLogMaintenance(contexts, DatabaseProviders.Get(DatabaseProvider.Sqlite));
            sw.Restart();
            var purged = await maintenance.PurgeByRetentionAsync("Delivered", retentionDays: 30);
            sw.Stop();
            Console.WriteLine($"VOLUME purge   {purged:N0} rows in {sw.ElapsedMilliseconds:N0}ms");

            // Loose bounds: these catch a collapse (a scan where a seek was intended), not a slow agent.
            Assert.True(firstPageMs < 2000, $"First page took {firstPageMs}ms at {rows:N0} rows - the newest-first index is not being used.");
            Assert.True(deepMs < firstPageMs + 2000, $"Page 20 took {deepMs}ms vs page 1 {firstPageMs}ms - keyset pagination has degraded to a scan.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(path + suffix);
        }
    }

    /// <summary>
    /// Loads <paramref name="rows"/> relay_log rows spread over 90 days, so retention purges and date
    /// filters have a realistic distribution to work against rather than every row sharing one timestamp.
    /// </summary>
    private static async Task BulkLoadAsync(IDbContextFactory<DispatchDbContext> contexts, int rows)
    {
        const int batch = 5_000;

        var start = DateTime.UtcNow.AddDays(-90);
        var span = TimeSpan.FromDays(90).TotalMilliseconds;

        for (var offset = 0; offset < rows; offset += batch)
        {
            var count = Math.Min(batch, rows - offset);
            await using var db = await contexts.CreateDbContextAsync();

            for (var i = 0; i < count; i++)
            {
                var n = offset + i;
                db.RelayLog.Add(new RelayLogEntity
                {
                    LoggedAt = start.AddMilliseconds(span * n / rows),
                    SpoolId = $"vol{n:D9}",
                    Event = n % 20 == 0 ? "Failed" : "Delivered",
                    Status = n % 20 == 0 ? "Error" : "OK",
                    FromAddress = $"sender{n % 1000}@example.com",
                    FromDomain = "example.com",
                    ToAddresses = "[]",
                    ToDomain = $"bulk-{n % 50}.example.net",
                    // One row in 10,000 carries the needle the unindexed LIKE probe looks for.
                    Subject = n % 10_000 == 42 ? $"needle-42 message {n}" : $"Bulk message {n}",
                    SizeBytes = 1024 + n % 8192,
                    RelayName = "bulk",
                    IngestSource = "SMTP",
                });
            }

            // One SaveChanges per batch, so the whole batch commits as a single transaction. Per-row commits
            // would measure fsync latency rather than anything about the engine's capacity.
            await db.SaveChangesAsync();
        }
    }
}
