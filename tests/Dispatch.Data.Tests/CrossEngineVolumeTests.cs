using System.Diagnostics;
using Dispatch.Core.Logging;
using Dispatch.Data.Providers;
using Dispatch.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Tests;

/// <summary>
/// Volume characterisation against whichever engine the fixture is running - so the scale profile of the
/// bring-your-own-server backends (PostgreSQL, MariaDB, SQL Server) is measured, not assumed to match
/// SQLite. <see cref="SqliteVolumeTests"/> covers the bundled default and its file/WAL specifics; this
/// covers the query-shape profile that must hold on every engine.
///
/// It deliberately exercises BOTH read paths, because they answer different questions and one of them was
/// just restructured:
///   * QueryAsync - the keyset cursor. Page N must cost the same as page 1 (index seek, not scan).
///   * PageAsync  - the dashboard list, which deduplicates lifecycle rows to one per message AND returns a
///                  total. The dedup grouping was rewritten to be sargable (GROUP BY spool_id, not a CASE
///                  key); this is where that pays off or does not on a large table.
///
/// Opt-in via DISPATCH_VOLUME_TEST (writes hundreds of thousands of rows, takes minutes). Row count via
/// DISPATCH_VOLUME_ROWS, default 250,000. Characterisation, not a millisecond gate: the assertions catch a
/// COLLAPSE (a scan where a seek was intended, or deep pages diverging from page 1), never a slow agent.
/// The printed VOLUME lines are the deliverable - they say whether each engine scales as expected.
/// </summary>
public class CrossEngineVolumeTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("DISPATCH_VOLUME_TEST") is { Length: > 0 } v
        && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    private static int TargetRows =>
        int.TryParse(Environment.GetEnvironmentVariable("DISPATCH_VOLUME_ROWS"), out var n) ? n : 250_000;

    [Fact]
    public async Task Message_log_scales_on_this_engine()
    {
        if (!Enabled || !sql.Available) return;

        var contexts = sql.Contexts;
        var provider = sql.DbProvider;
        var engine = sql.Engine;
        var rows = TargetRows;
        Console.WriteLine($"VOLUME [{engine}] target={rows:N0} rows");

        // ---- Bulk load. Batched SaveChanges (5,000): the ingest path commits per message, and that rate
        // is ConcurrentWriteTests' job; this measures whether the write side keeps up at all at volume.
        var sw = Stopwatch.StartNew();
        await BulkLoadAsync(contexts, rows);
        sw.Stop();
        Console.WriteLine($"VOLUME [{engine}] load    {rows:N0} rows in {sw.ElapsedMilliseconds:N0}ms ({rows * 1000.0 / sw.ElapsedMilliseconds:N0}/sec)");

        long sizeBytes;
        await using (var probe = await contexts.CreateDbContextAsync())
            sizeBytes = await provider.GetDatabaseSizeBytesAsync(probe);
        Console.WriteLine($"VOLUME [{engine}] size    {sizeBytes / 1024.0 / 1024.0:N1} MiB ({(double)sizeBytes / rows:N0} bytes/row)");

        var query = new SqlMessageLogQuery(contexts);

        // ---- Keyset page 1 (dashboard default view, newest-first) --------------------------------------
        sw.Restart();
        var firstPage = await query.QueryAsync(new MessageLogFilter { Limit = 50 });
        sw.Stop();
        var page1Ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"VOLUME [{engine}] keyset-p1  {firstPage.Rows.Count} rows in {page1Ms}ms");
        Assert.NotEmpty(firstPage.Rows);

        // ---- Deep keyset pagination: page 20 must track page 1, not row count -------------------------
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
        Console.WriteLine($"VOLUME [{engine}] keyset-p20 {deepMs}ms");

        // ---- Dashboard PageAsync: the deduplicating list + total. This is the sargable-rewrite path. ---
        sw.Restart();
        var paged = await query.PageAsync(new MessageLogFilter { Limit = 50 }, offset: 0);
        sw.Stop();
        var pageAsyncMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"VOLUME [{engine}] pageAsync  {paged.Rows.Count} rows, total {paged.Total:N0} in {pageAsyncMs}ms");
        Assert.NotEmpty(paged.Rows);

        // ---- Indexed filter --------------------------------------------------------------------------
        sw.Restart();
        var byDomain = await query.QueryAsync(new MessageLogFilter { ToDomain = "bulk-7.example.net", Limit = 50 });
        sw.Stop();
        Console.WriteLine($"VOLUME [{engine}] filter     to_domain -> {byDomain.Rows.Count} in {sw.ElapsedMilliseconds}ms");

        // ---- count(*) - linear on every engine; measured so its growth is on record ------------------
        sw.Restart();
        await using (var probe = await contexts.CreateDbContextAsync())
            await probe.RelayLog.LongCountAsync();
        sw.Stop();
        Console.WriteLine($"VOLUME [{engine}] count      full count(*) in {sw.ElapsedMilliseconds}ms");

        // ---- Purge, the operation that has to survive a backlog --------------------------------------
        var maintenance = new SqlLogMaintenance(contexts, provider);
        sw.Restart();
        var purged = await maintenance.PurgeByRetentionAsync("Delivered", retentionDays: 30);
        sw.Stop();
        Console.WriteLine($"VOLUME [{engine}] purge      {purged:N0} rows in {sw.ElapsedMilliseconds:N0}ms");

        // Loose bounds: a COLLAPSE, not a slow agent. Page 1 and deep pages are index seeks and must stay
        // sub-second-ish regardless of row count; if either balloons, an index is not being used.
        Assert.True(page1Ms < 3000, $"[{engine}] keyset page 1 took {page1Ms}ms at {rows:N0} rows - the newest-first index is not serving it.");
        Assert.True(deepMs < page1Ms + 3000, $"[{engine}] deep page took {deepMs}ms vs page 1 {page1Ms}ms - keyset pagination degraded to a scan.");
    }

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
                    Subject = n % 10_000 == 42 ? $"needle-42 message {n}" : $"Bulk message {n}",
                    SizeBytes = 1024 + n % 8192,
                    RelayName = "bulk",
                    IngestSource = "SMTP",
                });
            }
            await db.SaveChangesAsync();
        }
    }
}
