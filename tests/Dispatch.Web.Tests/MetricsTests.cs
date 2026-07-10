namespace Dispatch.Web.Tests;

[Collection("web")]
public class MetricsTests(WebTestHost host)
{
    [Fact]
    public async Task Metrics_endpoint_is_open_and_prometheus_formatted()
    {
        var res = await host.Web.GetAsync("/metrics");
        res.EnsureSuccessStatusCode();
        Assert.StartsWith("text/plain", res.Content.Headers.ContentType!.MediaType);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("dispatch_up 1", body);
        Assert.Contains("# TYPE dispatch_messages_today gauge", body);
        Assert.Contains("dispatch_spool_files{state=\"incoming\"}", body);
    }
}
