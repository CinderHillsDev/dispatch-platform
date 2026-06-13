using Dispatch.Core.Providers;

namespace Dispatch.Core.Routing;

/// <summary>The relay selected for a message, plus which routing rule matched (if any).</summary>
public sealed class ResolvedRelay
{
    public required RelayConfig Config { get; init; }

    public int Id => Config.Id;
    public string Name => Config.Name;
    public int MaxConcurrency => Config.MaxConcurrency;

    public int? MatchedRuleId { get; init; }
    public string? MatchedRuleName { get; init; }
    public bool RoutingMatched => MatchedRuleId.HasValue;
}
