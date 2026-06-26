using Dispatch.Core.Audit;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dispatch.Core.Spool;

/// <summary>
/// The relay worker pool (spec §6.5–§6.8). Recovers orphaned files on startup, then runs N
/// workers that claim spool files via per-relay semaphores and dispatch them through the
/// resolved <see cref="IRelayProvider"/>, applying the success / transient-retry / permanent-fail
/// outcomes. SQL is only touched after the provider responds, and never fatally.
/// </summary>
public sealed class SpoolWorkerPool : BackgroundService
{
    private readonly SpoolDirectory _spool;
    private readonly IRelayResolver _routing;
    private readonly IRelayProviderFactory _providerFactory;
    private readonly ILogRepository _logRepo;
    private readonly ILoggingSettings _loggingSettings;
    private readonly ICounterRepository _counters;
    private readonly MinuteCounterRing _minuteRing;
    private readonly RelayConcurrencyTracker _concurrency;
    private readonly ILogger<SpoolWorkerPool> _log;
    private readonly SpoolOptions _spoolOptions;
    private readonly IRetrySettings _retry;
    private readonly Dispatch.Core.Audit.IAuditLog? _audit;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();
    private readonly Dictionary<int, int> _semaphoreMax = new();   // relayId → the max the semaphore was sized for
    private readonly Lock _semLock = new();
    private FileSystemWatcher? _watcher;

    public SpoolWorkerPool(
        SpoolDirectory spool,
        IRelayResolver routing,
        IRelayProviderFactory providerFactory,
        ILogRepository logRepo,
        ILoggingSettings loggingSettings,
        ICounterRepository counters,
        MinuteCounterRing minuteRing,
        RelayConcurrencyTracker concurrency,
        IOptions<SpoolOptions> spoolOptions,
        IRetrySettings retry,
        ILogger<SpoolWorkerPool> log,
        Dispatch.Core.Audit.IAuditLog? audit = null)
    {
        _spool = spool;
        _routing = routing;
        _providerFactory = providerFactory;
        _logRepo = logRepo;
        _loggingSettings = loggingSettings;
        _counters = counters;
        _minuteRing = minuteRing;
        _concurrency = concurrency;
        _spoolOptions = spoolOptions.Value;
        _retry = retry;
        _log = log;
        _audit = audit;
    }

    /// <summary>Per-relay concurrency gates. Exposed internally for tests.</summary>
    internal ConcurrentDictionary<int, SemaphoreSlim> Semaphores => _semaphores;

    /// <summary>
    /// Test hook: simulate a dropped / never-firing FileSystemWatcher by disabling it, so that files
    /// added afterwards are discovered only via the worker-loop timeout fallback (spec §14.1).
    /// </summary>
    internal void DisableWatcherForTests()
    {
        if (_watcher is not null) _watcher.EnableRaisingEvents = false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RecoverOrphans();

        _watcher = new FileSystemWatcher(_spool.IncomingDir, "*.eml")
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => _spool.Signal(e.Name!);

        var workerCount = Math.Clamp(_spoolOptions.WorkerCount, 1, 32);
        _log.LogInformation("Starting {Count} relay worker(s)", workerCount);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => WorkerLoop(stoppingToken))
            .ToArray();

        // Pick up anything already sitting in incoming/ at startup.
        foreach (var f in Directory.EnumerateFiles(_spool.IncomingDir, "*.eml"))
            _spool.Signal(Path.GetFileName(f));

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _watcher.Dispose();
        }
    }

    /// <summary>
    /// FileSystemWatcher-fallback poll interval (spec §14.1). The OS can drop watcher events under heavy
    /// load, so the doorbell wait times out and the worker attempts a claim anyway — files are never
    /// stranded by a missed signal. Not latency-sensitive: it only matters when events are lost.
    /// </summary>
    private static readonly TimeSpan DoorbellTimeout = TimeSpan.FromSeconds(5);

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a doorbell signal, but cap the wait so a dropped FileSystemWatcher event can't
                // strand files: on timeout we fall through and still attempt a claim (fallback poll).
                await _spool.WaitAsync(ct).AsTask().WaitAsync(DoorbellTimeout, ct);
            }
            catch (TimeoutException)
            {
                // No signal within the window — fall through to the fallback claim attempt below.
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ClaimedFile? claimed;
            try
            {
                claimed = await ClaimFileForAvailableRelayAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (claimed is null) continue;

            var c = claimed.Value;
            _concurrency.Increment(c.Relay.Id);
            try
            {
                await ProcessAsync(c.EmlPath, c.Relay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error processing {File}", c.EmlPath);
            }
            finally
            {
                _concurrency.Decrement(c.Relay.Id);
                c.Semaphore.Release();
            }

            // A file just freed a relay slot — nudge a worker to re-scan for more work.
            _spool.Signal(Path.GetFileName(c.EmlPath));
        }
    }

    // ---- Orphan recovery (spec §6.8) -------------------------------------------------------

    internal void RecoverOrphans()
    {
        foreach (var eml in Directory.EnumerateFiles(_spool.ProcessingDir, "*.eml").ToList())
        {
            var fileName = Path.GetFileName(eml);
            var destEml = Path.Combine(_spool.IncomingDir, fileName);
            try
            {
                File.Move(eml, destEml, overwrite: false);
                var metaSrc = SpoolMeta.PathFor(eml);
                if (File.Exists(metaSrc))
                    File.Move(metaSrc, SpoolMeta.PathFor(destEml), overwrite: false);
                _log.LogWarning("Recovered orphaned spool file {File}", fileName);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to recover orphan {File}", fileName);
            }
        }
    }

    // ---- Relay-aware claim (spec §6.5) -----------------------------------------------------

    internal readonly record struct ClaimedFile(string EmlPath, SemaphoreSlim Semaphore, ResolvedRelay Relay);

    internal async Task<ClaimedFile?> ClaimFileForAvailableRelayAsync(CancellationToken ct)
    {
        var candidates = Directory.EnumerateFiles(_spool.IncomingDir, "*.eml").ToList();

        foreach (var candidate in candidates)
        {
            var meta = SpoolMeta.Peek(candidate);
            if (meta is null)
            {
                // Missing/corrupt .meta. Normally this is just the sub-second window while the sidecar is being
                // written; but a torn/corrupt sidecar would otherwise strand the .eml forever (the claim loop
                // skips it on every pass). Quarantine it to failed/ once it is older than the grace period.
                QuarantineUnreadable(candidate);
                continue;
            }

            if (meta.NextRetryAt is { } due && due > DateTime.UtcNow) continue;  // back-off not elapsed

            var relay = await _routing.ResolveAsync(meta.FromAddress, meta.ToAddresses, ct);
            var sem = GetSemaphoreFor(relay.Id, relay.MaxConcurrency);

            if (!sem.Wait(0)) continue;                                   // relay at capacity — try next file

            var dest = _spool.ProcessingPath(Path.GetFileName(candidate));
            try
            {
                File.Move(candidate, dest);                               // atomic claim — first worker wins
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException)
            {
                sem.Release();                                            // another worker beat us — move on
                continue;
            }
            catch
            {
                sem.Release();                                            // never leak the relay slot on an unexpected failure
                throw;
            }

            var metaSrc = SpoolMeta.PathFor(candidate);
            if (File.Exists(metaSrc))
            {
                try { File.Move(metaSrc, SpoolMeta.PathFor(dest), overwrite: true); }
                catch (IOException) { /* meta moves best-effort; Load below will surface real problems */ }
            }

            return new ClaimedFile(dest, sem, relay);
        }

        return null;
    }

    private static SemaphoreSlim CreateSemaphore(int maxConcurrency)
    {
        var max = maxConcurrency <= 0 ? int.MaxValue : maxConcurrency;
        return new SemaphoreSlim(max, max);
    }

    /// <summary>
    /// Returns the per-relay concurrency gate, creating it on first use and REPLACING it when the relay's
    /// max_concurrency has changed (spec §6.5). Workers already holding the old semaphore release the exact
    /// instance they acquired (carried in <see cref="ClaimedFile"/>), so swapping the map entry is safe.
    /// </summary>
    private SemaphoreSlim GetSemaphoreFor(int relayId, int maxConcurrency)
    {
        lock (_semLock)
        {
            if (_semaphores.TryGetValue(relayId, out var existing)
                && _semaphoreMax.TryGetValue(relayId, out var curMax) && curMax == maxConcurrency)
                return existing;

            var sem = CreateSemaphore(maxConcurrency);
            _semaphores[relayId] = sem;
            _semaphoreMax[relayId] = maxConcurrency;
            if (existing is not null)
                _log.LogInformation("Relay {Relay} max_concurrency changed to {Max}; concurrency gate resized", relayId, maxConcurrency);
            return sem;
        }
    }

    // ---- Dispatch + outcomes (spec §6.6) ---------------------------------------------------

    internal async Task ProcessAsync(string emlPath, ResolvedRelay relay, CancellationToken ct)
    {
        var meta = SpoolMeta.Load(emlPath);
        var retry = await _retry.GetAsync(ct);
        var sw = Stopwatch.StartNew();

        // Count each message as received exactly once (off the hot path). Guarded by a persisted flag rather
        // than RetryCount==0 so a crash-recovered file (moved processing→incoming and reprocessed while still
        // on its first attempt) is not double-counted, which would inflate the received total and throughput.
        if (!meta.ReceivedCounted)
        {
            await SafeIncrement(relay.Id, CounterField.Received, ct);
            _minuteRing.RecordReceived();
            meta.ReceivedCounted = true;
            try { meta.Save(emlPath); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not persist ReceivedCounted for {SpoolId}", meta.SpoolId); }
        }

        try
        {
            MimeMessage mime;
            await using (var fs = File.OpenRead(emlPath))
                mime = await MimeMessage.LoadAsync(fs, ct);

            // RFC 5322 requires a Date and providers penalise mail missing Date/Message-Id — add them if the
            // submitting client didn't (using the spool receipt time for Date). Existing values are untouched.
            MessageNormalizer.EnsureRequiredHeaders(
                mime, new DateTimeOffset(DateTime.SpecifyKind(meta.ReceivedAt, DateTimeKind.Utc)));

            var relayMessage = new RelayMessage
            {
                Message = mime,
                FromAddress = meta.FromAddress,
                ToAddresses = meta.ToAddresses,
                SpoolId = meta.SpoolId.ToString(),
                Tags = meta.Tags,
                SizeBytes = new FileInfo(emlPath).Length,
            };

            var provider = _providerFactory.Build(relay.Config);
            var result = await provider.SendAsync(relayMessage, ct);

            // Counters are always accurate; the relay_log row is best-effort.
            await SafeIncrement(relay.Id, CounterField.Delivered, ct);
            _minuteRing.RecordDelivered();
            if (await _loggingSettings.LogDeliveredAsync(ct))
                await SafeLog(new RelayLogEntry
            {
                Event = "Delivered",
                Status = "OK",
                SpoolId = meta.SpoolId.ToString(),
                FromAddress = meta.FromAddress,
                FromDomain = ExtractDomain(meta.FromAddress),
                ToAddresses = meta.ToAddresses,
                ToDomain = ExtractDomain(meta.ToAddresses.FirstOrDefault() ?? ""),
                Subject = mime.Subject,
                SizeBytes = (int)relayMessage.SizeBytes,
                RelayId = relay.Id,
                RelayName = relay.Name,
                RoutingRuleId = relay.MatchedRuleId,
                RoutingRuleName = relay.MatchedRuleName,
                RoutingMatched = relay.RoutingMatched,
                Provider = provider.Name,
                ProviderMessageId = result.ProviderMessageId,
                ProviderResponse = result.ProviderDetail,
                DurationMs = (int)sw.ElapsedMilliseconds,
                IngestSource = meta.IngestSource,
                SourceIp = meta.SourceIp,
                ApiKeyId = meta.ApiKeyId,
                ApiKeyName = meta.ApiKeyName,
                Tags = meta.Tags,
                XMailer = meta.XMailer,
                AttachmentCount = meta.AttachmentCount,
            }, ct);

            DeleteSpoolFiles(emlPath);
            _log.LogInformation(
                "Delivered {SpoolId} via {Relay} ({Provider}) in {Ms}ms",
                meta.SpoolId, relay.Name, provider.Name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (IsTransient(ex) && meta.RetryCount < retry.MaxRetries)
        {
            await SafeIncrement(relay.Id, CounterField.Retried, ct);
            if (await _loggingSettings.LogRetryingAsync(ct))
                await SafeLog(BuildErrorEntry("Retrying", meta, relay, ex, meta.RetryCount + 1), ct);

            meta.RetryCount++;
            var delay = retry.DelayFor(meta.RetryCount);
            meta.NextRetryAt = DateTime.UtcNow.Add(delay);
            meta.LastError = ex.Message;
            meta.LastRelayId = relay.Id;

            var incomingEml = MoveBackToIncoming(emlPath, meta);
            _log.LogWarning(
                "Transient failure for {SpoolId} (attempt {Attempt}): {Error}; retrying in {Delay}",
                meta.SpoolId, meta.RetryCount, ex.Message, delay);
            // Zero-delay retries get an immediate doorbell; delayed retries are picked up by the worker-loop
            // fallback poll once NextRetryAt elapses (the claim loop honours NextRetryAt). This avoids spawning
            // an unbounded number of fire-and-forget delay timers under a sustained provider outage.
            if (delay <= TimeSpan.Zero)
                _spool.Signal(Path.GetFileName(incomingEml));
        }
        catch (Exception ex)
        {
            await SafeIncrement(relay.Id, CounterField.Failed, ct);
            await SafeLog(BuildErrorEntry("Failed", meta, relay, ex, meta.RetryCount), ct);

            meta.LastError = ex.Message;
            meta.LastRelayId = relay.Id;
            MoveToFailed(emlPath, meta);
            _log.LogError(ex, "Permanent failure for {SpoolId}: {Error}", meta.SpoolId, ex.Message);
            if (_audit is not null)
                await _audit.Relay($"Delivery failed via relay \"{relay.Name}\" — moved to Retry Queue", ex.Message);
        }
    }

    // ---- File transitions ------------------------------------------------------------------

    private string MoveBackToIncoming(string processingEmlPath, SpoolMeta meta)
    {
        var fileName = Path.GetFileName(processingEmlPath);
        var destEml = Path.Combine(_spool.IncomingDir, fileName);

        File.Move(processingEmlPath, destEml, overwrite: true);
        meta.Save(destEml);

        var oldMeta = SpoolMeta.PathFor(processingEmlPath);
        if (File.Exists(oldMeta)) File.Delete(oldMeta);
        return destEml;
    }

    private void MoveToFailed(string emlPath, SpoolMeta meta)
    {
        var fileName = Path.GetFileName(emlPath);
        var destEml = _spool.FailedPath(fileName);
        File.Move(emlPath, destEml, overwrite: true);
        meta.Save(destEml);

        var oldMeta = SpoolMeta.PathFor(emlPath);
        if (File.Exists(oldMeta)) File.Delete(oldMeta);
    }

    private static void DeleteSpoolFiles(string emlPath)
    {
        File.Delete(emlPath);
        var metaPath = SpoolMeta.PathFor(emlPath);
        if (File.Exists(metaPath)) File.Delete(metaPath);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Grace period before an incoming .eml whose .meta is missing/unreadable is treated as corrupt rather
    /// than mid-write. Generously larger than the sub-second ingest write window (spec §6.8 orphan handling).
    /// </summary>
    private static readonly TimeSpan UnreadableMetaGrace = TimeSpan.FromMinutes(5);

    private void QuarantineUnreadable(string emlPath)
    {
        DateTime writtenUtc;
        try { writtenUtc = File.GetLastWriteTimeUtc(emlPath); }
        catch { return; }   // vanished (claimed/deleted by another worker) — nothing to do

        if (DateTime.UtcNow - writtenUtc < UnreadableMetaGrace) return;   // still within the mid-write window

        try
        {
            var dest = _spool.FailedPath(Path.GetFileName(emlPath));
            File.Move(emlPath, dest, overwrite: true);
            var meta = SpoolMeta.PathFor(emlPath);
            if (File.Exists(meta))
            {
                try { File.Move(meta, SpoolMeta.PathFor(dest), overwrite: true); } catch { /* best effort */ }
            }
            _log.LogError("Quarantined {File} to failed/: its .meta is missing or corrupt", Path.GetFileName(emlPath));
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            // Raced with another worker that claimed/removed it — fine, it's no longer stranded.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to quarantine unreadable spool file {File}", Path.GetFileName(emlPath));
        }
    }

    private static bool IsTransient(Exception ex) => ex is TransientRelayException;

    private static string ExtractDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "";
    }

    private RelayLogEntry BuildErrorEntry(
        string @event, SpoolMeta meta, ResolvedRelay relay, Exception ex, int retryAttempt) => new()
    {
        Event = @event,
        Status = "Error",
        SpoolId = meta.SpoolId.ToString(),
        RetryAttempt = retryAttempt,
        Error = ex.Message,
        FromAddress = meta.FromAddress,
        FromDomain = ExtractDomain(meta.FromAddress),
        ToAddresses = meta.ToAddresses,
        ToDomain = ExtractDomain(meta.ToAddresses.FirstOrDefault() ?? ""),
        RelayId = relay.Id,
        RelayName = relay.Name,
        RoutingRuleId = relay.MatchedRuleId,
        RoutingRuleName = relay.MatchedRuleName,
        RoutingMatched = relay.RoutingMatched,
        IngestSource = meta.IngestSource,
        SourceIp = meta.SourceIp,
        ApiKeyId = meta.ApiKeyId,
        ApiKeyName = meta.ApiKeyName,
        Tags = meta.Tags,
        XMailer = meta.XMailer,
        AttachmentCount = meta.AttachmentCount,
    };

    private async Task SafeLog(RelayLogEntry entry, CancellationToken ct)
    {
        try { await _logRepo.InsertAsync(entry, ct); }
        catch (Exception ex) { _log.LogError(ex, "relay_log insert failed (delivery unaffected)"); }
    }

    private async Task SafeIncrement(int? relayId, CounterField field, CancellationToken ct)
    {
        try { await _counters.IncrementAsync(relayId, field, ct); }
        catch (Exception ex) { _log.LogError(ex, "counter increment failed (delivery unaffected)"); }
    }
}
