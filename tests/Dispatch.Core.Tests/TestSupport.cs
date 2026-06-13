using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Collections.Concurrent;

namespace Dispatch.Core.Tests;

/// <summary>A throwaway spool directory under the OS temp dir, cleaned up on dispose.</summary>
public sealed class TempSpool : IDisposable
{
    public string Root { get; }
    public SpoolDirectory Spool { get; }

    public TempSpool()
    {
        Root = Path.Combine(Path.GetTempPath(), "dispatch-tests", Guid.NewGuid().ToString("N"));
        Spool = new SpoolDirectory(Root);
    }

    public int Count(string dir) => Directory.EnumerateFiles(dir, "*.eml").Count();

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>Test helpers for seeding spool files and building a worker pool with doubles.</summary>
public static class TestData
{
    public static string SampleEml(string from = "alice@example.com", string to = "bob@example.org",
        string subject = "Test", string? tag = null)
    {
        var tagHeader = tag is null ? "" : $"X-Dispatch-Tag: {tag}\r\n";
        return $"From: {from}\r\nTo: {to}\r\nSubject: {subject}\r\n{tagHeader}\r\nHello world\r\n";
    }

    public static (string EmlPath, Guid Id) Seed(string targetDir, SpoolDirectory spool,
        string from = "alice@example.com", string[]? to = null, int retryCount = 0, DateTime? nextRetryAt = null)
    {
        var id = Guid.NewGuid();
        var emlPath = Path.Combine(targetDir, $"{id}.eml");
        File.WriteAllText(emlPath, SampleEml(from));
        new SpoolMeta
        {
            SpoolId = id,
            ReceivedAt = DateTime.UtcNow,
            FromAddress = from,
            ToAddresses = to ?? ["bob@example.org"],
            RetryCount = retryCount,
            NextRetryAt = nextRetryAt,
        }.Save(emlPath);
        return (emlPath, id);
    }

    public static SpoolWorkerPool BuildPool(
        SpoolDirectory spool,
        IRelayProvider provider,
        ILogRepository logRepo,
        ICounterRepository counters,
        RelayConfig? relay = null,
        RetryOptions? retry = null,
        int workerCount = 4,
        ILoggingSettings? loggingSettings = null)
    {
        relay ??= new RelayConfig { Id = 1, Name = "default", Provider = RelayProviderType.Local };
        var resolver = new StubRelayResolver(new ResolvedRelay { Config = relay });
        var factory = new StubProviderFactory(provider);
        return new SpoolWorkerPool(
            spool, resolver, factory, logRepo, loggingSettings ?? new AlwaysLogSettings(), counters, new MinuteCounterRing(), new RelayConcurrencyTracker(),
            Options.Create(new SpoolOptions { WorkerCount = workerCount }),
            Options.Create(retry ?? new RetryOptions { MaxRetries = 3, DelaysSeconds = [0.01, 0.02, 0.03] }),
            NullLogger<SpoolWorkerPool>.Instance);
    }

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}

// ---- Test doubles -------------------------------------------------------------------------

public sealed class StubRelayResolver(ResolvedRelay relay) : IRelayResolver
{
    public ValueTask<ResolvedRelay> ResolveAsync(
        string fromAddress, IReadOnlyList<string> toAddresses, CancellationToken ct = default) =>
        ValueTask.FromResult(relay);
}

public sealed class StubProviderFactory(IRelayProvider provider) : IRelayProviderFactory
{
    public IRelayProvider Build(RelayConfig config) => provider;
}

public sealed class DelegateProvider(
    string name, Func<RelayMessage, CancellationToken, Task<RelayResult>> send) : IRelayProvider
{
    public string Name => name;
    public Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct) => send(message, ct);

    public static DelegateProvider AlwaysSucceeds() =>
        new("None", (_, _) => Task.FromResult(RelayResult.Success(id: "ok")));

    public static DelegateProvider AlwaysThrows(Exception ex) =>
        new("Boom", (_, _) => throw ex);
}

public sealed class CapturingLogRepository : ILogRepository
{
    private readonly ConcurrentQueue<RelayLogEntry> _entries = new();
    public IReadOnlyCollection<RelayLogEntry> Entries => _entries.ToArray();

    public Task InsertAsync(RelayLogEntry entry, CancellationToken ct = default)
    {
        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
