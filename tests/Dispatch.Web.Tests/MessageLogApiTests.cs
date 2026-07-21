using System.Net;
using System.Net.Http.Json;
using Dispatch.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class MessageLogApiTests(WebTestHost host)
{
    [Fact]
    public async Task Message_detail_returns_full_record_for_known_id()
    {
        // FakeMessageLogQuery yields a detail only for id 42.
        var res = await host.Web.GetAsync("/api/messages/42");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<DetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(42, body!.Id);
        Assert.Equal("spool-42", body.SpoolId);
        Assert.Contains("b@y.com", body.ToAddresses);
        Assert.Contains("urgent", body.Tags);
    }

    [Fact]
    public async Task Message_detail_returns_404_when_missing()
    {
        var res = await host.Web.GetAsync("/api/messages/99999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Message_list_maps_every_query_param_to_the_right_filter_field()
    {
        // The repository tests prove each filter selects the right rows; this proves the endpoint hands the
        // repository the right filter in the first place. Without it, a param wired to the wrong field (or a
        // comma-separated list not split, or page not turned into an offset) would pass every other test.
        var res = await host.Web.GetAsync(
            "/api/messages?from=2026-01-02T03:04:05Z&to=2026-02-03T04:05:06Z" +
            "&status=OK,Error&event=Delivered,Failed&source=API&apiKeyId=7" +
            "&fromDomain=from.example&toDomain=to.example&relay=relayX&rule=ruleY" +
            "&subject=hello%20world&tag=urgent&page=3&pageSize=25");
        res.EnsureSuccessStatusCode();

        var fake = (FakeMessageLogQuery)host.Services.GetRequiredService<IMessageLogQuery>();
        var f = fake.LastPageFilter;
        Assert.NotNull(f);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), f!.FromUtc);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6), f.ToUtc);
        Assert.Equal(["OK", "Error"], f.Statuses!);                // comma-separated, split
        Assert.Equal(["Delivered", "Failed"], f.Events!);
        Assert.Equal("API", f.IngestSource);
        Assert.Equal(7, f.ApiKeyId);
        Assert.Equal("from.example", f.FromDomain);
        Assert.Equal("to.example", f.ToDomain);
        Assert.Equal("relayX", f.RelayName);
        Assert.Equal("ruleY", f.RoutingRuleName);
        Assert.Equal("hello world", f.Subject);
        Assert.Equal("urgent", f.Tag);
        Assert.Equal(25, f.Limit);
        Assert.Equal(50, fake.LastPageOffset);                     // page 3 of 25 -> offset (3-1)*25
    }

    [Fact]
    public async Task Message_list_leaves_absent_params_unset()
    {
        // The mirror of the mapping test: omitted params must arrive as null/empty, not as "" - an empty
        // string on FromDomain would filter to rows whose domain is literally empty and hide everything.
        var res = await host.Web.GetAsync("/api/messages");
        res.EnsureSuccessStatusCode();

        var f = ((FakeMessageLogQuery)host.Services.GetRequiredService<IMessageLogQuery>()).LastPageFilter;
        Assert.NotNull(f);
        Assert.Null(f!.FromUtc);
        Assert.Null(f.ToUtc);
        Assert.Null(f.Statuses);
        Assert.Null(f.Events);
        Assert.Null(f.IngestSource);
        Assert.Null(f.ApiKeyId);
        Assert.Null(f.FromDomain);
        Assert.Null(f.ToDomain);
        Assert.Null(f.RelayName);
        Assert.Null(f.RoutingRuleName);
        Assert.Null(f.Subject);
        Assert.Null(f.Tag);
    }

    private sealed record DetailResponse(long Id, string SpoolId, string[] ToAddresses, string[] Tags);
}
