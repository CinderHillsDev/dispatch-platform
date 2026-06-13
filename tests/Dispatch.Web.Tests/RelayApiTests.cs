using System.Net.Http.Json;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class RelayApiTests(WebTestHost host)
{
    [Fact]
    public async Task Relay_secret_is_redacted_and_never_leaked()
    {
        var save = await host.Web.PutAsJsonAsync("/api/relays/1", new
        {
            name = "default",
            provider = "Mailgun",
            enabled = true,
            maxConcurrency = 4,
            // Dictionary keys serialize verbatim (PascalCase), matching the provider field schema.
            settings = new Dictionary<string, string> { ["ApiKey"] = "super-secret-key", ["Domain"] = "mg.example.com" },
        });
        save.EnsureSuccessStatusCode();

        var res = await host.Web.GetAsync("/api/relays/1");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret-key", json);   // the secret is never returned
        Assert.Contains("********", json);                 // it is shown redacted
        Assert.Contains("mg.example.com", json);           // non-secret values are shown
    }

    [Fact]
    public async Task Simulate_falls_back_to_default_when_no_rules()
    {
        var res = await host.Web.PostAsJsonAsync("/api/routing/simulate", new { from = "a@x.com", to = "b@y.com" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<SimulateResponse>();
        Assert.False(body!.Matched);
        Assert.Equal("default", body.RelayName);
    }

    private sealed record SimulateResponse(bool Matched, int? MatchedRuleId, string? MatchedRuleName, int RelayId, string RelayName, string Provider);
}
