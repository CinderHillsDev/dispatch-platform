using System.Collections.Concurrent;
using System.Diagnostics;
using Dispatch.Core.Providers;
using Microsoft.AspNetCore.SignalR;
using MimeKit;

namespace Dispatch.Web.Realtime;

/// <summary>One structured line of a provider-test run (spec §11.5).</summary>
public sealed record TestRunLine(DateTimeOffset Ts, string Level, string Message);

/// <summary>An in-memory provider-test run. Ephemeral UI state — never persisted to SQL (spec §11.5).</summary>
public sealed class TestRun
{
    public string RunId { get; } = $"tr_{Guid.NewGuid():N}"[..12];
    public string Provider { get; init; } = "";
    public string Status { get; set; } = "Running";   // Running | Success | Failed
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public List<TestRunLine> Lines { get; } = [];
}

/// <summary>Initiates a provider test (spec §11.4).</summary>
public sealed record TestProviderRequest(string Provider, Dictionary<string, string?>? Settings, string? TestRecipient);

/// <summary>
/// Runs a provider test against the credentials supplied in the request (no saving required), appending
/// structured log lines that are pushed live over <see cref="TestProviderHub"/> and held in memory for
/// polling (spec §11.5). Singleton: it owns the run store and a periodic eviction timer.
/// </summary>
public sealed class ProviderTestService : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly IRelayProviderFactory _factory;
    private readonly IHubContext<TestProviderHub> _hub;
    private readonly ConcurrentDictionary<string, TestRun> _runs = new();
    private readonly Timer _cleanup;

    public ProviderTestService(IRelayProviderFactory factory, IHubContext<TestProviderHub> hub)
    {
        _factory = factory;
        _hub = hub;
        _cleanup = new Timer(_ => EvictExpired(), null, Retention, Retention);
    }

    public TestRun? Get(string runId) => _runs.GetValueOrDefault(runId);

    /// <summary>Starts the test asynchronously and returns immediately with the new run id (spec §11.4).</summary>
    public TestRun StartTest(TestProviderRequest request)
    {
        if (!Enum.TryParse<RelayProviderType>(request.Provider, ignoreCase: true, out var providerType))
            throw new ArgumentException($"Unknown provider '{request.Provider}'.");

        var recipient = string.IsNullOrWhiteSpace(request.TestRecipient)
            ? throw new ArgumentException("A test recipient is required.")
            : request.TestRecipient!.Trim();

        var config = new RelayConfig
        {
            Name = "test",
            Provider = providerType,
            Settings = request.Settings ?? new Dictionary<string, string?>(),
        };

        var run = new TestRun { Provider = providerType.ToString() };
        _runs[run.RunId] = run;

        // Fire and forget — the caller gets the runId immediately; the test streams over SignalR.
        _ = Task.Run(() => ExecuteAsync(run, config, recipient));

        return run;
    }

    private async Task ExecuteAsync(TestRun run, RelayConfig config, string recipient)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await LogAsync(run, "Info", "Starting provider test");
            await LogAsync(run, "Info", $"Provider: {config.Provider}");

            var provider = _factory.Build(config);   // throws for Unconfigured / unsupported

            var message = BuildTestMessage(config.Provider, recipient);
            await LogAsync(run, "Info", "Building test MimeMessage");
            await LogAsync(run, "Info", $"From: {message.From}");
            await LogAsync(run, "Info", $"To:   {recipient}");
            await LogAsync(run, "Info", $"Calling {provider.Name}...");

            var relayMessage = new RelayMessage
            {
                Message = message,
                FromAddress = message.From.Mailboxes.First().Address,
                ToAddresses = [recipient],
            };

            var result = await provider.SendAsync(relayMessage, CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(result.ProviderDetail))
                await LogAsync(run, "Info", result.ProviderDetail!);
            if (!string.IsNullOrWhiteSpace(result.ProviderMessageId))
                await LogAsync(run, "Info", $"Message-ID: {result.ProviderMessageId}");

            run.DurationMs = sw.ElapsedMilliseconds;
            run.Status = "Success";
            await LogAsync(run, "Success", $"Test completed in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            await LogAsync(run, "Error", ex.Message);
            run.DurationMs = sw.ElapsedMilliseconds;
            run.Status = "Failed";
            await LogAsync(run, "Failed", $"Test failed after {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// Builds the spec §11.3 provider-test message: a Dispatch-Test From, a descriptive timestamped subject,
    /// both plain and HTML bodies, and the <c>X-Dispatch-Test: true</c> header. Shared with the relay-scoped
    /// test endpoint so both code paths send an identical, conformant message. <paramref name="fromOverride"/>
    /// lets a caller substitute the sender (e.g. a provider that requires a verified domain).
    /// </summary>
    public static MimeMessage BuildTestMessage(RelayProviderType provider, string recipient, string? fromOverride = null)
    {
        var hostname = Environment.MachineName;
        var version = typeof(ProviderTestService).Assembly.GetName().Version?.ToString() ?? "dev";
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

        var message = new MimeMessage();
        if (!string.IsNullOrWhiteSpace(fromOverride))
            message.From.Add(MailboxAddress.Parse(fromOverride));
        else
            message.From.Add(new MailboxAddress("Dispatch Test", $"dispatch-test@{hostname}"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = $"Dispatch provider test — {provider} — {timestamp}";
        message.Headers.Add("X-Dispatch-Test", "true");

        var text = $"Your {provider} relay credentials are working.\r\n\r\nDispatch version: {version}\r\nSent: {timestamp}\r\n";
        var html = $"<html><body><p>Your <strong>{provider}</strong> relay credentials are working.</p>" +
                   $"<p>Dispatch version: {version}<br/>Sent: {timestamp}</p></body></html>";

        var body = new BodyBuilder { TextBody = text, HtmlBody = html };
        message.Body = body.ToMessageBody();
        return message;
    }

    private async Task LogAsync(TestRun run, string level, string message)
    {
        var line = new TestRunLine(DateTimeOffset.UtcNow, level, message);
        run.Lines.Add(line);
        await _hub.Clients.Group(run.RunId).SendAsync("TestProviderLogLine", new
        {
            runId = run.RunId,
            ts = line.Ts,
            level = line.Level,
            message = line.Message,
        });
    }

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, run) in _runs)
            if (run.Status != "Running" && run.CreatedAt < cutoff)
                _runs.TryRemove(id, out _);
    }

    public void Dispose() => _cleanup.Dispose();
}
