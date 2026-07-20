using Dispatch.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>Reads/writes the ordered <c>routing_rules</c> table (spec §10.3, §10.5, §10.10).</summary>
public sealed class SqlRoutingRuleRepository(IDbContextFactory<DispatchDbContext> contexts) : IRoutingRuleRepository
{
    public async Task<IReadOnlyList<RoutingRule>> GetEnabledOrderedAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.RoutingRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);

        // Specificity is computed in code (spec §10.5: priority ASC, then specificity DESC), so the final
        // ordering is applied after materialising rather than in SQL.
        return rows.Select(ToRule)
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.Specificity)
            .ToList();
    }

    public async Task<IReadOnlyList<RoutingRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.RoutingRules.AsNoTracking().OrderBy(r => r.Priority).ToListAsync(ct);
        return rows.Select(ToRule).ToList();
    }

    public async Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // An unspecified priority goes to the end of the list. Reading the max and inserting share a
        // transaction because priority is UNIQUE - two concurrent creates would otherwise pick the same one.
        var priority = rule.Priority > 0
            ? rule.Priority
            : (await db.RoutingRules.MaxAsync(r => (int?)r.Priority, ct) ?? 0) + 10;

        var entity = new RoutingRuleEntity
        {
            Priority = priority,
            Name = rule.Name,
            RecipientPattern = rule.RecipientPattern,
            SenderPattern = rule.SenderPattern,
            RelayId = rule.RelayId,
            Enabled = rule.Enabled,
            CreatedAt = DateTime.UtcNow,
        };

        db.RoutingRules.Add(entity);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToRule(entity);
    }

    public async Task<bool> UpdateAsync(RoutingRule rule, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.RoutingRules
            .Where(r => r.Id == rule.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Name, rule.Name)
                .SetProperty(r => r.RecipientPattern, rule.RecipientPattern)
                .SetProperty(r => r.SenderPattern, rule.SenderPattern)
                .SetProperty(r => r.RelayId, rule.RelayId)
                .SetProperty(r => r.Enabled, rule.Enabled), ct) > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.RoutingRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<int> idsInPriorityOrder, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Two-phase, to dodge the UNIQUE(priority) constraint: park every row at a negative priority that
        // cannot collide with a real one, then assign the final values. Doing it in one pass would fail as
        // soon as a rule moved onto a priority another rule had not vacated yet.
        for (var i = 0; i < idsInPriorityOrder.Count; i++)
        {
            var id = idsInPriorityOrder[i];
            var parked = -(i + 1);
            await db.RoutingRules.Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Priority, parked), ct);
        }

        for (var i = 0; i < idsInPriorityOrder.Count; i++)
        {
            var id = idsInPriorityOrder[i];
            var priority = (i + 1) * 10;
            await db.RoutingRules.Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Priority, priority), ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> CountReferencingRelayAsync(int relayId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.RoutingRules.AsNoTracking().CountAsync(r => r.RelayId == relayId, ct);
    }

    private static RoutingRule ToRule(RoutingRuleEntity e) => new()
    {
        Id = e.Id, Priority = e.Priority, Name = e.Name, RecipientPattern = e.RecipientPattern,
        SenderPattern = e.SenderPattern, RelayId = e.RelayId, Enabled = e.Enabled,
    };
}
