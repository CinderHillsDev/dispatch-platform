using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Data.Repositories;

namespace Dispatch.Data.Tests;

/// <summary>Integration tests for relay CRUD + routing-rule repositories. Auto-skip without DISPATCH_TEST_SQL.</summary>
public class SqlRoutingTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task Relay_create_set_default_and_delete()
    {
        if (!sql.Available) return;
        var relays = new SqlRelayRepository(sql.Factory);

        var mg = await relays.CreateAsync("Mailgun-EU", RelayProviderType.Mailgun, 4, 0);
        Assert.Contains(await relays.GetAllAsync(), r => r.Id == mg.Id && r.Name == "Mailgun-EU");

        // Promote the new relay; the seeded "default" relay should be demoted.
        Assert.True(await relays.SetDefaultAsync(mg.Id));
        var def = await relays.GetDefaultAsync();
        Assert.Equal(mg.Id, def!.Id);

        // A non-default relay can be deleted.
        var temp = await relays.CreateAsync("Temp", RelayProviderType.None, 4, 0);
        Assert.True(await relays.DeleteAsync(temp.Id));
        Assert.DoesNotContain(await relays.GetAllAsync(), r => r.Id == temp.Id);
    }

    [Fact]
    public async Task Rules_create_order_reorder_and_reference_count()
    {
        if (!sql.Available) return;
        var relays = new SqlRelayRepository(sql.Factory);
        var rules = new SqlRoutingRuleRepository(sql.Factory);
        var relay = await relays.CreateAsync("Target", RelayProviderType.None, 4, 0);

        var a = await rules.CreateAsync(new RoutingRule { Name = "A", RecipientPattern = "*.acme.com", RelayId = relay.Id, Enabled = true });
        var b = await rules.CreateAsync(new RoutingRule { Name = "B", SenderPattern = "app.myco.com", RelayId = relay.Id, Enabled = true });
        Assert.True(b.Priority > a.Priority);   // auto-assigned ascending

        Assert.Equal(2, await rules.CountReferencingRelayAsync(relay.Id));

        // Reorder so B comes first; GetAll is priority-ordered.
        await rules.ReorderAsync([b.Id, a.Id]);
        var ordered = await rules.GetAllAsync();
        Assert.Equal(b.Id, ordered[0].Id);
        Assert.Equal(a.Id, ordered[1].Id);

        // Enabled+ordered query sorts by priority then specificity.
        var enabled = await rules.GetEnabledOrderedAsync();
        Assert.Equal([b.Id, a.Id], enabled.Select(r => r.Id).ToArray());

        Assert.True(await rules.DeleteAsync(a.Id));
        Assert.Equal(1, await rules.CountReferencingRelayAsync(relay.Id));
    }
}
