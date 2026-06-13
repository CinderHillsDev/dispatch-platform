using System.Net;
using System.Net.Http.Json;

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
    public async Task Message_list_accepts_relay_and_tag_filters()
    {
        // The fake returns an empty page; we only assert the query params are accepted and the route resolves.
        var res = await host.Web.GetAsync("/api/messages?relay=default&tag=urgent&fromDomain=x.com");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ListResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Rows);
    }

    private sealed record DetailResponse(long Id, string SpoolId, string[] ToAddresses, string[] Tags);
    private sealed record ListResponse(object[] Rows);
}
