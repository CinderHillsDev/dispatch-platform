using Dispatch.Core.ApiKeys;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Configuration;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Smtp;
using Dispatch.Core.Spool;
using Dispatch.Web;
using Dispatch.Web.Endpoints;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.Web.Tests;

/// <summary>
/// Boots a real Kestrel host (the endpoints branch on the real local port, so a real listener is
/// needed — TestServer reports port 0) with in-memory fakes instead of SQL. Shared across a test class.
/// </summary>
public sealed class WebTestHost : IAsyncLifetime
{
    public const int WebPort = 18080;
    public const int ApiPort = 18091;
    public const int ApiTlsPort = 18444;
    public const string ValidKey = "dsp_live_validkey00000000000000000000000";
    public const string LimitedKey = "dsp_live_limited000000000000000000000000";
    public const string OtherKey = "dsp_live_other00000000000000000000000000";

    private WebApplication _app = null!;
    public string SpoolDir { get; } = Path.Combine(Path.GetTempPath(), "dispatch-web-tests", Guid.NewGuid().ToString("N"));
    public HttpClient Web { get; private set; } = null!;
    public HttpClient Api { get; private set; } = null!;
    public HttpClient ApiTls { get; private set; } = null!;
    public SpoolDirectory Spool { get; private set; } = null!;
    public Dispatch.Core.Maintenance.IntakeState Intake { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Spool = new SpoolDirectory(SpoolDir);

        // A self-signed cert for the HTTPS ingestion-API listener (mirrors the shared TLS cert in prod).
        var (certPath, certPw) = Dispatch.Web.Endpoints.TlsCert.Generate(SpoolDir, "localhost");
        var apiTlsCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, certPw);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(WebPort);
            k.ListenLocalhost(ApiPort);
            k.ListenLocalhost(ApiTlsPort, lo => lo.UseHttps(apiTlsCert));
        });

        builder.Services.Configure<ApiOptions>(o =>
        {
            o.Port = ApiPort;
            o.AllowedCidrs = ["127.0.0.1/32", "::1/128"];
            o.RateLimitPerKey = 100;
        });
        // /health reads EffectivePorts; with no Ports set this falls back to DefaultPorts (2525).
        builder.Services.Configure<ListenerOptions>(_ => { });

        builder.Services.AddSingleton(Spool);
        Intake = new Dispatch.Core.Maintenance.IntakeState();
        builder.Services.AddSingleton(Intake);
        // The SMTP listener isn't started in tests, so this stays at its default (no listening ports);
        // /health reports configuredPorts (from ListenerOptions) plus an empty listeningPorts.
        builder.Services.AddSingleton<Dispatch.Core.Maintenance.SmtpListenerState>();

        // ConfigCache is the runtime source of truth (spec §12.5). Seed defaults into the fake config repo —
        // overriding the ports the test host actually listens on so ApiKeyMiddleware (which reads api.port
        // live) routes correctly — then load the cache from it. Seeding the repo (not just the cache) means a
        // PUT /api/config that reloads the cache reconstructs every key, not only the ones it wrote.
        var fakeConfig = new FakeConfigRepository();
        var seed = new Dictionary<string, string>(ConfigDefaults.Defaults, StringComparer.OrdinalIgnoreCase)
        {
            [ConfigKeys.ApiPort] = ApiPort.ToString(),
            [ConfigKeys.ApiTlsEnabled] = "true",
            [ConfigKeys.ApiTlsPort] = ApiTlsPort.ToString(),
            [ConfigKeys.ApiRateLimitPerKey] = "100",
            [ConfigKeys.WebUiPort] = WebPort.ToString(),
        };
        foreach (var (k, v) in seed) await fakeConfig.SetAsync(k, v);
        var configCache = new ConfigCache();
        await configCache.LoadAsync(fakeConfig);
        builder.Services.AddSingleton(configCache);
        builder.Services.AddSingleton<IConfigRepository>(fakeConfig);
        builder.Services.AddSingleton<MinuteCounterRing>();
        builder.Services.AddSingleton<RelayConcurrencyTracker>();
        var counters = new InMemoryCounterRepository();
        builder.Services.AddSingleton<ICounterReader>(counters);
        builder.Services.AddSingleton<Dispatch.Core.Counters.ICounterRepository>(counters);
        builder.Services.AddSingleton<Dispatch.Core.Logging.ILoggingSettings, Dispatch.Core.Logging.AlwaysLogSettings>();
        builder.Services.AddSingleton<IApiKeyRepository, FakeApiKeyRepository>();
        builder.Services.AddSingleton<IMessageLogQuery, FakeMessageLogQuery>();
        builder.Services.AddSingleton<IRelayRepository, FakeRelayRepository>();
        builder.Services.AddSingleton<IRelaySettingsStore, FakeRelaySettingsStore>();
        builder.Services.AddSingleton<IRoutingRuleRepository, FakeRoutingRuleRepository>();
        builder.Services.AddSingleton<IDatabaseHealth, FakeDatabaseHealth>();
        builder.Services.AddSingleton<ILogMaintenance, FakeLogMaintenance>();
        builder.Services.AddSingleton<Dispatch.Core.Maintenance.IStorageReport, FakeStorageReport>();
        builder.Services.AddSingleton<ISmtpCredentialRepository, FakeSmtpCredentialRepository>();
        builder.Services.AddSingleton<IRelayProviderFactory, FakeProviderFactory>();
        builder.Services.AddSingleton<IRelayResolver, RoutingEngine>();
        builder.Services.AddSingleton<Dispatch.Core.Audit.IAuditLog, FakeAuditLog>();
        builder.Services.AddSingleton<ILogRepository, NullLogRepository>();   // decorated by AddDispatchWeb
        builder.Services.AddDispatchWeb();

        _app = builder.Build();
        _app.UseMiddleware<ApiKeyMiddleware>();
        _app.MapIngestionApi(new ApiOptions { Port = ApiPort, HttpEnabled = true, TlsEnabled = true, TlsPort = ApiTlsPort });
        _app.MapDashboardApi(WebPort);
        _app.MapHub<LogHub>("/hub/logs");
        _app.MapHub<TestProviderHub>("/hub/test-provider");

        await _app.StartAsync();

        Web = new HttpClient { BaseAddress = new Uri($"http://localhost:{WebPort}") };
        Api = new HttpClient { BaseAddress = new Uri($"http://localhost:{ApiPort}") };
        // Ignore the self-signed cert so the test can exercise the HTTPS ingestion path.
        var tlsHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        ApiTls = new HttpClient(tlsHandler) { BaseAddress = new Uri($"https://localhost:{ApiTlsPort}") };
    }

    public async Task DisposeAsync()
    {
        Web.Dispose();
        Api.Dispose();
        ApiTls.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(SpoolDir, recursive: true); } catch { /* best effort */ }
    }
}

internal sealed class FakeAuditLog : Dispatch.Core.Audit.IAuditLog
{
    private readonly List<Dispatch.Core.Audit.AuditEntry> _entries = new();
    private long _id;

    public Task WriteAsync(string kind, string category, string @event, string severity,
        string? actor, string? sourceIp, string? detail, CancellationToken ct = default)
    {
        lock (_entries)
            _entries.Insert(0, new Dispatch.Core.Audit.AuditEntry(
                ++_id, DateTime.UtcNow, kind, category, @event, severity, actor, sourceIp, detail));
        return Task.CompletedTask;
    }

    public Task<Dispatch.Core.Audit.AuditPage> QueryAsync(Dispatch.Core.Audit.AuditFilter filter, CancellationToken ct = default)
    {
        lock (_entries)
        {
            IEnumerable<Dispatch.Core.Audit.AuditEntry> q = _entries;
            if (!string.IsNullOrWhiteSpace(filter.Kind)) q = q.Where(e => e.Kind == filter.Kind);
            if (!string.IsNullOrWhiteSpace(filter.Category)) q = q.Where(e => e.Category == filter.Category);
            if (!string.IsNullOrWhiteSpace(filter.Severity)) q = q.Where(e => e.Severity == filter.Severity);
            if (!string.IsNullOrWhiteSpace(filter.Search))
                q = q.Where(e => (e.Event + " " + e.Detail + " " + e.Actor + " " + e.Category)
                    .Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
            var rows = q.Take(Math.Clamp(filter.Limit, 1, 200)).ToList();
            return Task.FromResult(new Dispatch.Core.Audit.AuditPage(rows, null));
        }
    }

    public Task<int> PurgeAsync(int generalRetentionDays, int securityRetentionDays, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeApiKeyRepository : IApiKeyRepository
{
    private readonly Dictionary<string, ApiKey> _byRaw = new()
    {
        [WebTestHost.ValidKey] = new ApiKey { Id = 1, KeyId = "dsp_live_val", Name = "valid", RateLimitPerMinute = 0 },
        [WebTestHost.LimitedKey] = new ApiKey { Id = 2, KeyId = "dsp_live_lim", Name = "limited", RateLimitPerMinute = 2 },
        [WebTestHost.OtherKey] = new ApiKey { Id = 3, KeyId = "dsp_live_oth", Name = "other", RateLimitPerMinute = 0 },
    };

    public Task<ApiKey?> VerifyAsync(string rawKey, CancellationToken ct = default) =>
        Task.FromResult(_byRaw.GetValueOrDefault(rawKey));

    public Task RecordUsageAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ApiKeyCreated> CreateAsync(string name, int rateLimitPerMinute, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<ApiKey>> ListAsync(bool includeRevoked = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>(_byRaw.Values.ToList());
    public Task<bool> RevokeAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class FakeMessageLogQuery : IMessageLogQuery
{
    public Task<MessageLogPage> QueryAsync(MessageLogFilter filter, CancellationToken ct = default) =>
        Task.FromResult(new MessageLogPage([], null));

    public Task<MessageLogPaged> PageAsync(MessageLogFilter filter, int offset, CancellationToken ct = default) =>
        Task.FromResult(new MessageLogPaged([], 0));
    public Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, int? apiKeyId, CancellationToken ct = default) =>
        Task.FromResult<MessageLogRow?>(null);
    public Task<IReadOnlyList<MessageLogRow>> RecentByApiKeyAsync(int apiKeyId, int limit, string[]? statuses, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageLogRow>>(apiKeyId == 1
            ? [new MessageLogRow { Id = 7, Status = "OK", Event = "Delivered", SpoolId = "spool-7", FromAddress = "a@x.com", ToDomain = "y.com", IngestSource = "API" }]
            : []);
    public Task<MessageLogDetail?> GetByIdAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<MessageLogDetail?>(id == 42
            ? new MessageLogDetail
            {
                Id = 42, Status = "OK", Event = "Delivered", SpoolId = "spool-42",
                FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
                RelayName = "default", Provider = "None", IngestSource = "API", Tags = ["urgent"],
            }
            : null);
}

internal sealed class FakeRelayRepository : Dispatch.Core.Relays.IRelayRepository
{
    private static readonly Dispatch.Core.Relays.RelayRecord Default = new()
    {
        Id = 1, Name = "default", Provider = Dispatch.Core.Providers.RelayProviderType.Local,
        IsDefault = true, Enabled = true, MaxConcurrency = 4,
    };
    public Task<Dispatch.Core.Relays.RelayRecord?> GetDefaultAsync(CancellationToken ct = default) => Task.FromResult<Dispatch.Core.Relays.RelayRecord?>(Default);
    public Task<IReadOnlyList<Dispatch.Core.Relays.RelayRecord>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Dispatch.Core.Relays.RelayRecord>>([Default]);
    public Task<Dispatch.Core.Relays.RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<Dispatch.Core.Relays.RelayRecord?>(Default);
    public Task<Dispatch.Core.Relays.RelayRecord> CreateAsync(string name, RelayProviderType provider, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default) => Task.FromResult(Default);
    public Task<bool> UpdateAsync(int id, string name, RelayProviderType provider, bool enabled, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> SetDefaultAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class FakeRelaySettingsStore : Dispatch.Core.Relays.IRelaySettingsStore
{
    private Dispatch.Core.Relays.RelaySettings _settings = Dispatch.Core.Relays.RelaySettings.Empty;
    public Task<Dispatch.Core.Relays.RelaySettings> GetAsync(int relayId, CancellationToken ct = default) => Task.FromResult(_settings);
    public Task SaveAsync(int relayId, Dispatch.Core.Relays.RelaySettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakeDatabaseHealth : IDatabaseHealth
{
    public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class FakeLogMaintenance : ILogMaintenance
{
    // 142 MB so /health's dbSizeMb is asserted as 142 in the web test.
    public Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default) => Task.FromResult(142L * 1024 * 1024);
    public Task<int> PurgeByRetentionAsync(string @event, int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> PurgeOldestAsync(int batchSize, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeStorageReport : Dispatch.Core.Maintenance.IStorageReport
{
    public Task<Dispatch.Core.Maintenance.DbStorage> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new Dispatch.Core.Maintenance.DbStorage(
            Connected: true, DatabaseBytes: 142L * 1024 * 1024, RelayLogBytes: 1024,
            RelayLogByEvent: [new Dispatch.Core.Maintenance.LogEventCount("Delivered", 3)],
            AuditBytes: 256, AuditRows: 5, AuditSecurityRows: 1));
}

internal sealed class FakeSmtpCredentialRepository : Dispatch.Core.Smtp.ISmtpCredentialRepository
{
    public Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<Dispatch.Core.Smtp.SmtpCredential>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Dispatch.Core.Smtp.SmtpCredential>>([]);
    public Task AddAsync(string username, string password, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> DeleteAsync(string username, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class FakeConfigRepository : IConfigRepository
{
    private readonly Dictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, bool encrypted = false, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
    public Task<IReadOnlyList<ConfigEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConfigEntry>>(_values.Select(kv => new ConfigEntry(kv.Key, kv.Value, false, DateTime.UtcNow)).ToList());
}

internal sealed class FakeRoutingRuleRepository : IRoutingRuleRepository
{
    private readonly List<RoutingRule> _rules = [];
    public Task<IReadOnlyList<RoutingRule>> GetEnabledOrderedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RoutingRule>>(_rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ToList());
    public Task<IReadOnlyList<RoutingRule>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoutingRule>>(_rules);
    public Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct = default) { _rules.Add(rule); return Task.FromResult(rule); }
    public Task<bool> UpdateAsync(RoutingRule rule, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
    public Task ReorderAsync(IReadOnlyList<int> idsInPriorityOrder, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountReferencingRelayAsync(int relayId, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeProviderFactory : Dispatch.Core.Providers.IRelayProviderFactory
{
    public Dispatch.Core.Providers.IRelayProvider Build(Dispatch.Core.Providers.RelayConfig config) => new FakeProvider();

    private sealed class FakeProvider : Dispatch.Core.Providers.IRelayProvider
    {
        public string Name => "Local";
        public Task<Dispatch.Core.Providers.RelayResult> SendAsync(Dispatch.Core.Providers.RelayMessage message, CancellationToken ct) =>
            Task.FromResult(Dispatch.Core.Providers.RelayResult.Success("test"));
    }
}
