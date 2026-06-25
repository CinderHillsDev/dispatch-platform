using System.Net;
using System.Net.Http.Json;
using Dispatch.Core.Providers;
using Dispatch.Web.Realtime;

namespace Dispatch.Web.Tests;

public class ProviderTestSenderTests
{
    private static Dictionary<string, string?> S(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Azure_default_from_is_first_verified_mailfrom()
    {
        var from = ProviderTestService.DefaultTestFrom(RelayProviderType.AzureCommunication,
            S(("MailFrom", "noreply@example.com, other@example.com")));
        Assert.Equal("noreply@example.com", from);
    }

    [Fact]
    public void Azure_without_mailfrom_has_no_default_from()
    {
        Assert.Null(ProviderTestService.DefaultTestFrom(RelayProviderType.AzureCommunication, S(("MailFrom", ""))));
        Assert.Null(ProviderTestService.DefaultTestFrom(RelayProviderType.AzureCommunication, S()));
    }

    [Fact]
    public void Mailgun_default_from_uses_verified_domain()
    {
        Assert.Equal("dispatch-test@mg.example.com",
            ProviderTestService.DefaultTestFrom(RelayProviderType.Mailgun, S(("Domain", "mg.example.com"))));
    }

    [Fact]
    public void Suggestions_dedupe_and_preserve_order()
    {
        var sug = ProviderTestService.AzureMailFromSuggestions("a@x.com; A@X.com, b@x.com");
        Assert.Equal(["a@x.com", "b@x.com"], sug);
    }
}

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
