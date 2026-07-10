using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;

namespace Dispatch.Core.Tests;

public class SpoolWorkerPoolTests
{
    private static ResolvedRelay Relay(int id = 1, int maxConcurrency = 4) =>
        new() { Config = new RelayConfig { Id = id, Name = "default", MaxConcurrency = maxConcurrency } };

    [Fact]
    public void RecoverOrphans_moves_processing_files_back_to_incoming()
    {
        using var t = new TempSpool();
        var (_, id) = TestData.Seed(t.Spool.ProcessingDir, t.Spool);
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), new InMemoryCounterRepository());

        pool.RecoverOrphans();

        Assert.Equal(0, t.Count(t.Spool.ProcessingDir));
        Assert.Equal(1, t.Count(t.Spool.IncomingDir));
        Assert.True(File.Exists(t.Spool.IncomingPath(id)));
        Assert.True(File.Exists(SpoolMeta.PathFor(t.Spool.IncomingPath(id))));
    }

    [Fact]
    public async Task Claim_is_atomic_exactly_one_concurrent_winner()
    {
        using var t = new TempSpool();
        TestData.Seed(t.Spool.IncomingDir, t.Spool);
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), new InMemoryCounterRepository(),
            relay: new RelayConfig { Id = 1, MaxConcurrency = 0 }); // unlimited - isolate the file-move race

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => pool.ClaimFileForAvailableRelayAsync(CancellationToken.None)));

        Assert.Equal(1, attempts.Count(c => c is not null));
        Assert.Equal(1, t.Count(t.Spool.ProcessingDir));
        Assert.Equal(0, t.Count(t.Spool.IncomingDir));
    }

    [Fact]
    public async Task Claim_skips_file_when_relay_at_capacity_then_picks_it_up_when_slot_frees()
    {
        using var t = new TempSpool();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), new InMemoryCounterRepository(),
            relay: new RelayConfig { Id = 1, MaxConcurrency = 1 });

        TestData.Seed(t.Spool.IncomingDir, t.Spool);
        var first = await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None);
        Assert.NotNull(first);                       // claimed; relay slot now held

        TestData.Seed(t.Spool.IncomingDir, t.Spool);
        var blocked = await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None);
        Assert.Null(blocked);                        // relay at capacity - second file skipped

        pool.Semaphores[1].Release();                // free the slot
        var afterRelease = await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None);
        Assert.NotNull(afterRelease);                // now the second file is claimable
    }

    [Fact]
    public async Task Claim_skips_file_whose_backoff_has_not_elapsed()
    {
        using var t = new TempSpool();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), new InMemoryCounterRepository());

        TestData.Seed(t.Spool.IncomingDir, t.Spool, nextRetryAt: DateTime.UtcNow.AddMinutes(5));

        var claimed = await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None);
        Assert.Null(claimed);
    }

    [Fact]
    public async Task ProcessAsync_success_deletes_spool_files_and_counts_delivered()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var log = new CapturingLogRepository();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(), log, counters);

        var (emlPath, _) = TestData.Seed(t.Spool.ProcessingDir, t.Spool);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);

        Assert.False(File.Exists(emlPath));
        Assert.False(File.Exists(SpoolMeta.PathFor(emlPath)));
        Assert.Equal(0, t.Count(t.Spool.FailedDir));
        Assert.Equal(1, counters.Get(1, CounterField.Delivered));
        Assert.Contains(log.Entries, e => e.Event == "Delivered" && e.Status == "OK");
    }

    private sealed class SuppressDeliveredSettings : Dispatch.Core.Logging.ILoggingSettings
    {
        public ValueTask<bool> LogDeliveredAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> LogRetryingAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> LogDeniedAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task Delivered_log_is_suppressed_but_counter_still_increments()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var log = new CapturingLogRepository();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(), log, counters,
            loggingSettings: new SuppressDeliveredSettings());

        var (emlPath, _) = TestData.Seed(t.Spool.ProcessingDir, t.Spool);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);

        Assert.Equal(1, counters.Get(1, CounterField.Delivered));            // counter always written
        Assert.DoesNotContain(log.Entries, e => e.Event == "Delivered");      // log row suppressed
    }

    [Fact]
    public async Task ProcessAsync_transient_failure_requeues_with_backoff()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var log = new CapturingLogRepository();
        var provider = DelegateProvider.AlwaysThrows(new TransientRelayException("upstream 421"));
        var pool = TestData.BuildPool(t.Spool, provider, log, counters,
            retry: new RetryOptions { MaxRetries = 3, DelaysSeconds = [60] });

        var (emlPath, id) = TestData.Seed(t.Spool.ProcessingDir, t.Spool, retryCount: 0);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);

        // moved back to incoming for retry
        var incomingEml = t.Spool.IncomingPath(id);
        Assert.True(File.Exists(incomingEml));
        Assert.Equal(0, t.Count(t.Spool.ProcessingDir));

        var meta = SpoolMeta.Load(incomingEml);
        Assert.Equal(1, meta.RetryCount);
        Assert.NotNull(meta.NextRetryAt);
        Assert.Equal(1, counters.Get(1, CounterField.Retried));
        Assert.Contains(log.Entries, e => e.Event == "Retrying");
    }

    [Fact]
    public async Task ProcessAsync_moves_to_failed_when_retries_exhausted()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var log = new CapturingLogRepository();
        var provider = DelegateProvider.AlwaysThrows(new TransientRelayException("still failing"));
        var pool = TestData.BuildPool(t.Spool, provider, log, counters,
            retry: new RetryOptions { MaxRetries = 3, DelaysSeconds = [0.01] });

        // RetryCount already at the max → the transient catch filter is skipped → permanent.
        var (emlPath, id) = TestData.Seed(t.Spool.ProcessingDir, t.Spool, retryCount: 3);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);

        Assert.True(File.Exists(t.Spool.FailedPath($"{id}.eml")));
        Assert.Equal(0, t.Count(t.Spool.ProcessingDir));
        Assert.Equal(1, counters.Get(1, CounterField.Failed));
        Assert.Contains(log.Entries, e => e.Event == "Failed");
    }

    [Fact]
    public async Task ProcessAsync_non_transient_failure_goes_straight_to_failed()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var provider = DelegateProvider.AlwaysThrows(new InvalidOperationException("bad config"));
        var pool = TestData.BuildPool(t.Spool, provider, new CapturingLogRepository(), counters);

        var (emlPath, id) = TestData.Seed(t.Spool.ProcessingDir, t.Spool, retryCount: 0);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);

        Assert.True(File.Exists(t.Spool.FailedPath($"{id}.eml")));
        Assert.Equal(1, counters.Get(1, CounterField.Failed));
    }

    [Fact]
    public async Task Worker_drains_file_via_timeout_fallback_when_no_signal_is_sent()
    {
        // Spec §14.1 FileSystemWatcher fallback: if a Created event is dropped (here, simulated by
        // disabling the watcher), the worker's bounded doorbell wait times out (~5s) and it still
        // attempts a claim - so a file added after startup, with no signal, is never stranded.
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), counters, workerCount: 1);

        await pool.StartAsync(CancellationToken.None);
        try
        {
            pool.DisableWatcherForTests();   // simulate a dropped/never-fired FileSystemWatcher event

            // Seed AFTER start, with the watcher disabled and without calling Signal: only the
            // timeout fallback poll can discover this file.
            TestData.Seed(t.Spool.IncomingDir, t.Spool);

            var delivered = await TestData.WaitUntil(
                () => counters.Get(1, CounterField.Delivered) == 1, timeoutMs: 12_000);
            Assert.True(delivered, "file was not drained by the timeout fallback");
            Assert.Equal(0, t.Count(t.Spool.IncomingDir));
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EndToEnd_started_pool_delivers_a_seeded_message()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), counters, workerCount: 2);

        // Seed before start so the startup sweep picks it up.
        var (_, id) = TestData.Seed(t.Spool.IncomingDir, t.Spool);

        await pool.StartAsync(CancellationToken.None);
        try
        {
            var delivered = await TestData.WaitUntil(() => counters.Get(1, CounterField.Delivered) == 1);
            Assert.True(delivered, "message was not delivered within the timeout");
            Assert.Equal(0, t.Count(t.Spool.IncomingDir));
            Assert.Equal(0, t.Count(t.Spool.ProcessingDir));
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Received_is_counted_once_across_retries_and_recovery()
    {
        using var t = new TempSpool();
        var counters = new InMemoryCounterRepository();
        var provider = DelegateProvider.AlwaysThrows(new TransientRelayException("flap"));
        var pool = TestData.BuildPool(t.Spool, provider, new CapturingLogRepository(), counters,
            retry: new RetryOptions { MaxRetries = 5, DelaysSeconds = [0] });

        var (emlPath, id) = TestData.Seed(t.Spool.ProcessingDir, t.Spool, retryCount: 0);
        await pool.ProcessAsync(emlPath, Relay(), CancellationToken.None);   // attempt 1: transient → back to incoming
        Assert.Equal(1, counters.Get(1, CounterField.Received));

        // Simulate crash-recovery: the file (still on its first delivery, ReceivedCounted persisted) is moved
        // back to processing and reprocessed. Received must NOT be counted a second time.
        var incoming = t.Spool.IncomingPath(id);
        var processing = t.Spool.ProcessingPath($"{id}.eml");
        File.Move(incoming, processing, overwrite: true);
        File.Move(SpoolMeta.PathFor(incoming), SpoolMeta.PathFor(processing), overwrite: true);
        await pool.ProcessAsync(processing, Relay(), CancellationToken.None);   // attempt 2

        Assert.Equal(1, counters.Get(1, CounterField.Received));   // counted once, not twice
        Assert.Equal(2, counters.Get(1, CounterField.Retried));    // both attempts retried
    }

    [Fact]
    public async Task Claim_quarantines_incoming_file_with_unreadable_meta_after_grace()
    {
        using var t = new TempSpool();
        var pool = TestData.BuildPool(t.Spool, DelegateProvider.AlwaysSucceeds(),
            new CapturingLogRepository(), new InMemoryCounterRepository());

        // A recent .eml with no .meta is still mid-write - left alone.
        var fresh = Guid.NewGuid();
        File.WriteAllText(t.Spool.IncomingPath(fresh), "raw");
        Assert.Null(await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None));
        Assert.True(File.Exists(t.Spool.IncomingPath(fresh)));

        // An old .eml whose .meta never appeared (torn/corrupt) is quarantined to failed/ so it can't strand.
        var stale = Guid.NewGuid();
        var staleEml = t.Spool.IncomingPath(stale);
        File.WriteAllText(staleEml, "raw");
        File.SetLastWriteTimeUtc(staleEml, DateTime.UtcNow.AddMinutes(-10));

        Assert.Null(await pool.ClaimFileForAvailableRelayAsync(CancellationToken.None));
        Assert.False(File.Exists(staleEml));
        Assert.True(File.Exists(t.Spool.FailedPath($"{stale}.eml")));
        Assert.True(File.Exists(t.Spool.IncomingPath(fresh)));   // the fresh one is untouched
    }
}
