using Dapper;
using Dispatch.Core.Routing;

namespace Dispatch.Data.Repositories;

/// <summary>Reads/writes the ordered <c>routing_rules</c> table (spec §10.3, §10.5, §10.10).</summary>
public sealed class SqlRoutingRuleRepository(SqlConnectionFactory factory) : IRoutingRuleRepository
{
    private const string SelectColumns =
        "id, priority, name, recipient_pattern AS RecipientPattern, sender_pattern AS SenderPattern, relay_id AS RelayId, enabled";
    private const string InsertedColumns =
        "id, priority, name, recipient_pattern AS \"RecipientPattern\", sender_pattern AS \"SenderPattern\", relay_id AS \"RelayId\", enabled";

    public async Task<IReadOnlyList<RoutingRule>> GetEnabledOrderedAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM routing_rules WHERE enabled", cancellationToken: ct));
        // Specificity is computed in code (spec §10.5: priority ASC, then specificity DESC).
        return rows.Select(r => r.ToRule())
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.Specificity)
            .ToList();
    }

    public async Task<IReadOnlyList<RoutingRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM routing_rules ORDER BY priority", cancellationToken: ct));
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var priority = rule.Priority > 0
            ? rule.Priority
            : (await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT MAX(priority) FROM routing_rules", cancellationToken: ct)) ?? 0) + 10;

        const string sql = $"""
            INSERT INTO routing_rules (priority, name, recipient_pattern, sender_pattern, relay_id, enabled)
            VALUES (@priority, @Name, @RecipientPattern, @SenderPattern, @RelayId, @Enabled)
            RETURNING {InsertedColumns};
            """;
        var row = await cn.QuerySingleAsync<Row>(new CommandDefinition(sql, new
        {
            priority, rule.Name, rule.RecipientPattern, rule.SenderPattern, rule.RelayId, rule.Enabled,
        }, cancellationToken: ct));
        return row.ToRule();
    }

    public async Task<bool> UpdateAsync(RoutingRule rule, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE routing_rules SET name = @Name, recipient_pattern = @RecipientPattern,
                                     sender_pattern = @SenderPattern, relay_id = @RelayId, enabled = @Enabled
            WHERE id = @Id;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(sql, new
        {
            rule.Id, rule.Name, rule.RecipientPattern, rule.SenderPattern, rule.RelayId, rule.Enabled,
        }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM routing_rules WHERE id = @id", new { id }, cancellationToken: ct));
        return n > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<int> idsInPriorityOrder, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            // Two-phase to dodge the UNIQUE(priority) constraint: park at negatives, then set final values.
            for (var i = 0; i < idsInPriorityOrder.Count; i++)
                await cn.ExecuteAsync(new CommandDefinition(
                    "UPDATE routing_rules SET priority = @p WHERE id = @id",
                    new { p = -(i + 1), id = idsInPriorityOrder[i] }, tx, cancellationToken: ct));

            for (var i = 0; i < idsInPriorityOrder.Count; i++)
                await cn.ExecuteAsync(new CommandDefinition(
                    "UPDATE routing_rules SET priority = @p WHERE id = @id",
                    new { p = (i + 1) * 10, id = idsInPriorityOrder[i] }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> CountReferencingRelayAsync(int relayId, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM routing_rules WHERE relay_id = @relayId", new { relayId }, cancellationToken: ct));
    }

    private sealed class Row
    {
        public int Id { get; init; }
        public int Priority { get; init; }
        public string Name { get; init; } = "";
        public string? RecipientPattern { get; init; }
        public string? SenderPattern { get; init; }
        public int RelayId { get; init; }
        public bool Enabled { get; init; }

        public RoutingRule ToRule() => new()
        {
            Id = Id, Priority = Priority, Name = Name, RecipientPattern = RecipientPattern,
            SenderPattern = SenderPattern, RelayId = RelayId, Enabled = Enabled,
        };
    }
}
