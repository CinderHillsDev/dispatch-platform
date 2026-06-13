using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;

namespace Dispatch.Core.Tests;

public class RoutingEngineTests
{
    private sealed class StubRelayRepo(RelayRecord? record) : IRelayRepository
    {
        public Task<RelayRecord?> GetDefaultAsync(CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelayRecord>>(record is null ? [] : [record]);
        public Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(record);
    }

    private sealed class StubSettings(RelaySettings settings) : IRelaySettingsStore
    {
        public Task<RelaySettings> GetAsync(int relayId, CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveAsync(int relayId, RelaySettings s, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Resolves_default_relay_identity_from_sql_and_provider_from_settings()
    {
        var record = new RelayRecord
        {
            Id = 2, Name = "primary", Provider = RelayProviderType.Smtp,
            MaxConcurrency = 7, IsDefault = true, Enabled = true,
        };
        var settings = new RelaySettings(RelayProviderType.Smtp,
            new Dictionary<string, string?> { ["Host"] = "smtp.example.com" });

        var engine = new RoutingEngine(new StubRelayRepo(record), new StubSettings(settings));
        var relay = await engine.ResolveAsync("a@x.com", ["b@y.com"]);

        Assert.Equal(2, relay.Id);
        Assert.Equal("primary", relay.Name);
        Assert.Equal(RelayProviderType.Smtp, relay.Config.Provider);
        Assert.Equal(7, relay.MaxConcurrency);
        Assert.Equal("smtp.example.com", relay.Config.Settings["Host"]);
        Assert.False(relay.RoutingMatched);
    }
}
