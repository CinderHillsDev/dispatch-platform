using System.Net;
using System.Net.Http.Json;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class ProviderTestApiTests(WebTestHost host)
{
    private sealed record StartResponse(string RunId, string Status);
    private sealed record RunLine(string Ts, string Level, string Message);
    private sealed record RunResponse(string RunId, string Status, string Provider, long DurationMs, RunLine[] Lines);

    [Fact]
    public async Task Test_provider_returns_run_id_and_reaches_terminal_status()
    {
        var start = await host.Web.PostAsJsonAsync("/api/config/test-provider", new
        {
            provider = "Local",
            settings = new Dictionary<string, string>(),
            testRecipient = "you@example.com",
        });
        start.EnsureSuccessStatusCode();

        var started = await start.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(started);
        Assert.StartsWith("tr_", started!.RunId);

        // Poll until the run reaches a terminal status (Local provider — no external call).
        RunResponse? run = null;
        for (var i = 0; i < 50 && (run is null || run.Status == "Running"); i++)
        {
            await Task.Delay(50);
            run = await host.Web.GetFromJsonAsync<RunResponse>($"/api/config/test-provider/{started.RunId}");
        }

        Assert.NotNull(run);
        Assert.Equal("Success", run!.Status);
        Assert.NotEmpty(run.Lines);
        Assert.Contains(run.Lines, l => l.Level == "Success");
    }

    [Fact]
    public async Task Test_provider_requires_recipient()
    {
        var res = await host.Web.PostAsJsonAsync("/api/config/test-provider", new
        {
            provider = "Local",
            settings = new Dictionary<string, string>(),
            testRecipient = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_run_id_returns_not_found()
    {
        var res = await host.Web.GetAsync("/api/config/test-provider/tr_doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
