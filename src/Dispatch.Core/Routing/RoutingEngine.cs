using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Core.Routing;

/// <summary>
/// Resolves the relay for a message. Routing rules are not yet implemented, so every message goes to the
/// default relay: its identity (id/name/max_concurrency) comes from the SQL <c>relays</c> table, and its
/// provider type + credentials come from <see cref="IRelaySettingsStore"/> (the SQL <c>config</c> table,
/// secrets decrypted). The interface (<see cref="ResolveAsync"/>) is unchanged so the rule engine can
/// drop in later (§10).
/// </summary>
public sealed class RoutingEngine(IRelayRepository relays, IRelaySettingsStore settings) : IRelayResolver
{
    public async ValueTask<ResolvedRelay> ResolveAsync(
        string fromAddress, IReadOnlyList<string> toAddresses, CancellationToken ct = default)
    {
        var record = await relays.GetDefaultAsync(ct);
        var id = record?.Id ?? 1;
        var relaySettings = await settings.GetAsync(id, ct);

        var config = new RelayConfig
        {
            Id = id,
            Name = record?.Name ?? "default",
            Provider = relaySettings.Provider,
            MaxConcurrency = record?.MaxConcurrency ?? 4,
            MaxMessageBytes = record?.MaxMessageBytes ?? 0,
            Settings = relaySettings.Settings,
        };
        return new ResolvedRelay { Config = config };
    }
}
