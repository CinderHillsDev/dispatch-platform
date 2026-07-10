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
    public async Task Azure_blocks_saving_an_smtp_relay_on_outbound_port_25()
    {
        host.Cloud.IsAzure = true;
        try
        {
            // Effective port 25 (SMTP provider defaults blank -> 25) is rejected with an explanation.
            var blocked = await host.Web.PutAsJsonAsync("/api/relays/1", new
            {
                name = "smarthost", provider = "Smtp", enabled = true, maxConcurrency = 4,
                settings = new Dictionary<string, string> { ["Host"] = "mail.example.com", ["Port"] = "25" },
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Contains("port 25", await blocked.Content.ReadAsStringAsync());

            // Port 587 is fine, even on Azure.
            var ok = await host.Web.PutAsJsonAsync("/api/relays/1", new
            {
                name = "smarthost", provider = "Smtp", enabled = true, maxConcurrency = 4,
                settings = new Dictionary<string, string> { ["Host"] = "mail.example.com", ["Port"] = "587" },
            });
            ok.EnsureSuccessStatusCode();
        }
        finally
        {
            host.Cloud.IsAzure = false;
        }
    }

    [Fact]
    public async Task Off_azure_an_smtp_relay_on_port_25_is_allowed()
    {
        Assert.False(host.Cloud.IsAzure);   // default
        var save = await host.Web.PutAsJsonAsync("/api/relays/1", new
        {
            name = "smarthost", provider = "Smtp", enabled = true, maxConcurrency = 4,
            settings = new Dictionary<string, string> { ["Host"] = "mail.example.com", ["Port"] = "25" },
        });
        save.EnsureSuccessStatusCode();
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
