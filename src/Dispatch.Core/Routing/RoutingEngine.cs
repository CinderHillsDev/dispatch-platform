using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Core.Routing;

/// <summary>
/// Resolves the relay for a message (spec §10): evaluates enabled routing rules in priority/specificity
/// order against the sender + first-recipient domains, falling back to the default relay when none match.
/// The matched relay's identity comes from the <c>relays</c> table and its provider/credentials from
/// <see cref="IRelaySettingsStore"/> (SQL <c>config</c>, secrets decrypted).
/// </summary>
public sealed class RoutingEngine(
    IRoutingRuleRepository rules,
    IRelayRepository relays,
    IRelaySettingsStore settings) : IRelayResolver
{
    public async ValueTask<ResolvedRelay> ResolveAsync(
        string fromAddress, IReadOnlyList<string> toAddresses, CancellationToken ct = default)
    {
        var fromDomain = DomainMatcher.ExtractDomain(fromAddress);
        var toDomain = DomainMatcher.ExtractDomain(toAddresses.FirstOrDefault() ?? "");

        foreach (var rule in await rules.GetEnabledOrderedAsync(ct))
        {
            var recipientMatch = rule.RecipientPattern is null || DomainMatcher.Matches(toDomain, rule.RecipientPattern);
            var senderMatch = rule.SenderPattern is null || DomainMatcher.Matches(fromDomain, rule.SenderPattern);
            if (recipientMatch && senderMatch)
            {
                var matched = await relays.GetByIdAsync(rule.RelayId, ct);
                if (matched is { Enabled: true })
                    return await BuildAsync(matched, rule.Id, rule.Name, ct);
                // Referenced relay missing/disabled → fall through to default.
            }
        }

        var fallback = await relays.GetDefaultAsync(ct);
        return await BuildAsync(fallback, matchedRuleId: null, matchedRuleName: null, ct);
    }

    private async ValueTask<ResolvedRelay> BuildAsync(
        RelayRecord? record, int? matchedRuleId, string? matchedRuleName, CancellationToken ct)
    {
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
        return new ResolvedRelay { Config = config, MatchedRuleId = matchedRuleId, MatchedRuleName = matchedRuleName };
    }
}
