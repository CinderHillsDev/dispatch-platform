namespace Dispatch.Core.Routing;

/// <summary>A row from the SQL <c>routing_rules</c> table (spec §10.3).</summary>
public sealed class RoutingRule
{
    public int Id { get; init; }
    public int Priority { get; set; }
    public string Name { get; init; } = "";
    public string? RecipientPattern { get; init; }   // null = match any recipient
    public string? SenderPattern { get; init; }       // null = match any sender
    public int RelayId { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Combined specificity used to break ties within the same priority (spec §10.5).</summary>
    public int Specificity =>
        DomainMatcher.Specificity(RecipientPattern) + DomainMatcher.Specificity(SenderPattern);
}

/// <summary>Reads/writes the ordered routing rule table (spec §10.3, §10.10).</summary>
public interface IRoutingRuleRepository
{
    /// <summary>Enabled rules ordered by priority ASC then specificity DESC (spec §10.5).</summary>
    Task<IReadOnlyList<RoutingRule>> GetEnabledOrderedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoutingRule>> GetAllAsync(CancellationToken ct = default);
    Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct = default);
    Task<bool> UpdateAsync(RoutingRule rule, CancellationToken ct = default);
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    Task ReorderAsync(IReadOnlyList<int> idsInPriorityOrder, CancellationToken ct = default);
    Task<int> CountReferencingRelayAsync(int relayId, CancellationToken ct = default);
}
