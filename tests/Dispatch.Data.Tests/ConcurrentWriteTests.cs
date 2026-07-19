using System.Diagnostics;
using Dapper;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;

namespace Dispatch.Data.Tests;

/// <summary>
/// Exercises the write pattern SpoolWorkerPool actually produces: many threads in one process, each logging
/// a message's lifecycle rows and bumping the daily counters, with no coordination between them.
///
/// This is the test that matters for the SQLite backend. SQLite permits exactly one writer at a time
/// database-wide, so the question is not whether writes serialise (they do) but whether they serialise
/// *gracefully* - waiting on the write lock - or fail with SQLITE_BUSY / "database is locked". That is a
/// configuration property, not an inherent limit: it depends on WAL plus busy_timeout, both set in
/// SqliteDialect. Without them this test fails; that is precisely why it exists.
///
/// It runs against whichever engine the fixture selected, so the same concurrency guarantee is asserted for
/// Postgres too.
/// </summary>
public class ConcurrentWriteTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Concurrent_writers_all_commit_without_lock_errors()
    {
        if (!sql.Available) return;

        const int writers = 16;
        const int messagesEach = 25;
        const int total = writers * messagesEach;

        var log = new SqlLogRepository(sql.Factory);
        var counters = new SqlCounterRepository(sql.Factory);

        // A relay row to attribute the counters to; relay_id is a real FK on relay_counters' sibling tables.
        await using (var cn = await sql.Factory.OpenAsync())
        {
            await cn.ExecuteAsync(
                "INSERT INTO relays (name, provider, is_default, enabled) VALUES (@n, 'Smtp', false, true);",
                new { n = "conc-" + Guid.NewGuid().ToString("N")[..8] });
        }
        int relayId;
        await using (var cn = await sql.Factory.OpenAsync())
            relayId = await cn.ExecuteScalarAsync<int>("SELECT MAX(id) FROM relays;");

        var sw = Stopwatch.StartNew();

        // No throttling and no retry loop: if the backend needs either to survive its own concurrency, the
        // application would have to know that, and this test should fail rather than paper over it.
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < messagesEach; i++)
            {
                var spoolId = $"{w:D3}-{i:D4}-{Guid.NewGuid():N}";
                await log.InsertAsync(new RelayLogEntry
                {
                    Event = "Delivered",
                    Status = "OK",
                    SpoolId = spoolId,
                    FromAddress = "sender@example.com",
                    FromDomain = "example.com",
                    ToAddresses = ["recipient@example.net"],
                    ToDomain = "example.net",
                    Subject = $"Concurrency probe {w}/{i}",
                    RelayId = relayId,
                    RelayName = "conc",
                    SizeBytes = 2048,
                });
                await counters.IncrementAsync(relayId, CounterField.Received);
                await counters.IncrementAsync(relayId, CounterField.Delivered);
            }
        })));

        sw.Stop();

        // Every row committed: no writer silently lost its work to a lock timeout.
        await using var verify = await sql.Factory.OpenAsync();
        var rows = await verify.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relay_log WHERE relay_name = 'conc';");
        Assert.Equal(total, rows);

        // The counter upserts are the contended path - every writer targets the same (date, relay_id) row,
        // so this is where a lost update would show up. Read-modify-write under a unique constraint is only
        // safe because it is a single atomic UPSERT statement; if it were a SELECT-then-UPDATE this would
        // come up short.
        var byRelay = await counters.GetTodayByRelayAsync();
        var mine = byRelay.Single(r => r.RelayId == relayId);
        Assert.Equal(total, mine.Received);
        Assert.Equal(total, mine.Delivered);

        var writes = total * 3;   // one log insert + two counter upserts per message
        Console.WriteLine(
            $"CONCURRENCY [{sql.Engine}] {writers} writers x {messagesEach} msgs = {writes} writes " +
            $"in {sw.ElapsedMilliseconds}ms ({writes * 1000.0 / sw.ElapsedMilliseconds:F0} writes/sec)");
    }
}
