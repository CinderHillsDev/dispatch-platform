using Dispatch.Core.ApiKeys;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Configuration;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
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
    public const string ValidKey = "dsp_live_validkey00000000000000000000000";
    public const string LimitedKey = "dsp_live_limited000000000000000000000000";

    private WebApplication _app = null!;
    public string SpoolDir { get; } = Path.Combine(Path.GetTempPath(), "dispatch-web-tests", Guid.NewGuid().ToString("N"));
    public HttpClient Web { get; private set; } = null!;
    public HttpClient Api { get; private set; } = null!;
    public SpoolDirectory Spool { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Spool = new SpoolDirectory(SpoolDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(WebPort);
            k.ListenLocalhost(ApiPort);
        });

        builder.Services.Configure<ApiOptions>(o =>
        {
            o.Port = ApiPort;
            o.AllowedCidrs = ["127.0.0.1/32", "::1/128"];
            o.RateLimitPerKey = 100;
        });

        builder.Services.AddSingleton(Spool);
        builder.Services.AddSingleton<MinuteCounterRing>();
        builder.Services.AddSingleton<ICounterReader, InMemoryCounterRepository>();
        builder.Services.AddSingleton<IApiKeyRepository, FakeApiKeyRepository>();
        builder.Services.AddSingleton<IMessageLogQuery, FakeMessageLogQuery>();
        builder.Services.AddSingleton<IRelayRepository, FakeRelayRepository>();
        builder.Services.AddSingleton<IRelaySettingsStore, FakeRelaySettingsStore>();
        builder.Services.AddSingleton<IRoutingRuleRepository, FakeRoutingRuleRepository>();
        builder.Services.AddSingleton<IRelayProviderFactory, FakeProviderFactory>();
        builder.Services.AddSingleton<IRelayResolver, RoutingEngine>();
        builder.Services.AddSingleton<ILogRepository, NullLogRepository>();   // decorated by AddDispatchWeb
        builder.Services.AddDispatchWeb();

        _app = builder.Build();
        _app.UseMiddleware<ApiKeyMiddleware>();
        _app.MapIngestionApi(ApiPort);
        _app.MapDashboardApi(WebPort);
        _app.MapHub<LogHub>("/hub/logs");

        await _app.StartAsync();

        Web = new HttpClient { BaseAddress = new Uri($"http://localhost:{WebPort}") };
        Api = new HttpClient { BaseAddress = new Uri($"http://localhost:{ApiPort}") };
    }

    public async Task DisposeAsync()
    {
        Web.Dispose();
        Api.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(SpoolDir, recursive: true); } catch { /* best effort */ }
    }
}

internal sealed class FakeApiKeyRepository : IApiKeyRepository
{
    private readonly Dictionary<string, ApiKey> _byRaw = new()
    {
        [WebTestHost.ValidKey] = new ApiKey { Id = 1, KeyId = "dsp_live_val", Name = "valid", RateLimitPerMinute = 0 },
        [WebTestHost.LimitedKey] = new ApiKey { Id = 2, KeyId = "dsp_live_lim", Name = "limited", RateLimitPerMinute = 2 },
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
    public Task<MessageLogRow?> GetBySpoolIdAsync(string spoolId, CancellationToken ct = default) =>
        Task.FromResult<MessageLogRow?>(null);
}

internal sealed class FakeRelayRepository : Dispatch.Core.Relays.IRelayRepository
{
    private static readonly Dispatch.Core.Relays.RelayRecord Default = new()
    {
        Id = 1, Name = "default", Provider = Dispatch.Core.Providers.RelayProviderType.None,
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
    private Dispatch.Core.Relays.RelaySettings _settings = Dispatch.Core.Relays.RelaySettings.None;
    public Task<Dispatch.Core.Relays.RelaySettings> GetAsync(int relayId, CancellationToken ct = default) => Task.FromResult(_settings);
    public Task SaveAsync(int relayId, Dispatch.Core.Relays.RelaySettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
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
        public string Name => "None";
        public Task<Dispatch.Core.Providers.RelayResult> SendAsync(Dispatch.Core.Providers.RelayMessage message, CancellationToken ct) =>
            Task.FromResult(Dispatch.Core.Providers.RelayResult.Success("test"));
    }
}
