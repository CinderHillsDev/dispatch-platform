using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;

namespace Dispatch.Core.Tests;

public class RoutingEngineTests
{
    private sealed class StubRelayRepo(params RelayRecord[] records) : IRelayRepository
    {
        public Task<RelayRecord?> GetDefaultAsync(CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.IsDefault) ?? records.FirstOrDefault());
        public Task<IReadOnlyList<RelayRecord>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelayRecord>>(records);
        public Task<RelayRecord?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.Id == id));
        public Task<RelayRecord> CreateAsync(string name, RelayProviderType provider, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(int id, string name, RelayProviderType provider, bool enabled, int maxConcurrency, long maxMessageBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetDefaultAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSettings(RelaySettings settings) : IRelaySettingsStore
    {
        public Task<RelaySettings> GetAsync(int relayId, CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveAsync(int relayId, RelaySettings s, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubRules(params RoutingRule[] rules) : IRoutingRuleRepository
    {
        public Task<IReadOnlyList<RoutingRule>> GetEnabledOrderedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RoutingRule>>(rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenByDescending(r => r.Specificity).ToList());
        public Task<IReadOnlyList<RoutingRule>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoutingRule>>(rules);
        public Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(RoutingRule rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReorderAsync(IReadOnlyList<int> idsInPriorityOrder, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountReferencingRelayAsync(int relayId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static RelayRecord Relay(int id, string name, bool isDefault = false) => new()
    {
        Id = id, Name = name, Provider = RelayProviderType.None, IsDefault = isDefault, Enabled = true, MaxConcurrency = 4,
    };

    [Fact]
    public async Task Falls_back_to_default_relay_when_no_rule_matches()
    {
        var engine = new RoutingEngine(
            new StubRules(),
            new StubRelayRepo(Relay(2, "primary", isDefault: true)),
            new StubSettings(new RelaySettings(RelayProviderType.Smtp, new Dictionary<string, string?> { ["Host"] = "h" })));

        var relay = await engine.ResolveAsync("a@x.com", ["b@y.com"]);

        Assert.Equal(2, relay.Id);
        Assert.Equal("primary", relay.Name);
        Assert.Equal(RelayProviderType.Smtp, relay.Config.Provider);
        Assert.False(relay.RoutingMatched);
    }

    [Theory]
    // Worked examples from spec §10.5.
    [InlineData("noreply@app.myco.com", "user@billing.acme.com", 10, "Mailgun-EU")]
    [InlineData("noreply@app.myco.com", "user@gmail.com", 20, "SendGrid")]
    [InlineData("ci@other.com", "tester@staging.myco.com", 30, "None-dev")]
    public async Task Matches_expected_rule(string from, string to, int expectedRuleId, string expectedRelay)
    {
        var relays = new StubRelayRepo(
            Relay(1, "Mailgun-US", isDefault: true), Relay(10, "Mailgun-EU"),
            Relay(20, "SendGrid"), Relay(30, "None-dev"));
        var rules = new StubRules(
            new RoutingRule { Id = 10, Priority = 10, Name = "acme", RecipientPattern = "*.acme.com", RelayId = 10, Enabled = true },
            new RoutingRule { Id = 20, Priority = 20, Name = "app", SenderPattern = "app.myco.com", RelayId = 20, Enabled = true },
            new RoutingRule { Id = 30, Priority = 30, Name = "staging", RecipientPattern = "staging.myco.com", RelayId = 30, Enabled = true });
        var engine = new RoutingEngine(rules, relays, new StubSettings(RelaySettings.None));

        var relay = await engine.ResolveAsync(from, [to]);

        Assert.True(relay.RoutingMatched);
        Assert.Equal(expectedRuleId, relay.MatchedRuleId);
        Assert.Equal(expectedRelay, relay.Name);
    }

    [Fact]
    public async Task Unmatched_addresses_use_default()
    {
        var relays = new StubRelayRepo(Relay(1, "Mailgun-US", isDefault: true), Relay(10, "Mailgun-EU"));
        var rules = new StubRules(
            new RoutingRule { Id = 10, Priority = 10, Name = "acme", RecipientPattern = "*.acme.com", RelayId = 10, Enabled = true });
        var engine = new RoutingEngine(rules, relays, new StubSettings(RelaySettings.None));

        var relay = await engine.ResolveAsync("ci@other.com", ["user@gmail.com"]);

        Assert.False(relay.RoutingMatched);
        Assert.Equal("Mailgun-US", relay.Name);
    }
}
