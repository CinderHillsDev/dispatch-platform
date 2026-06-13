using System.Net.Http.Json;
using System.Text.Json;

namespace Dispatch.Web.Tests;

[Collection("web")]
public class SettingsApiTests(WebTestHost host)
{
    [Fact]
    public async Task Get_returns_defaults_when_nothing_persisted()
    {
        var res = await host.Web.GetAsync("/api/settings");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Logging toggles default on.
        Assert.True(json.GetProperty("logging").GetProperty("delivered").GetBoolean());
        // Retry + retention sections exist with the appsettings defaults.
        Assert.True(json.GetProperty("retry").GetProperty("maxRetries").GetInt32() >= 0);
        Assert.True(json.GetProperty("retention").GetProperty("logDeliveredRetentionDays").GetInt32() >= 0);
    }

    [Fact]
    public async Task Put_persists_retry_and_retention_and_get_returns_them()
    {
        var put = await host.Web.PutAsJsonAsync("/api/settings", new
        {
            retry = new { maxRetries = 7, retryDelaysSeconds = new[] { 15.0, 90.0, 450.0 } },
            retention = new
            {
                logDeliveredRetentionDays = 21,
                logFailedRetentionDays = 100,
                spoolFailedRetentionDays = 60,
                capturedRetentionDays = 5,
                sizeTriggerGb = 8.25,
                sizeTargetGb = 7.75,
            },
        });
        put.EnsureSuccessStatusCode();

        var res = await host.Web.GetAsync("/api/settings");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var retry = json.GetProperty("retry");
        Assert.Equal(7, retry.GetProperty("maxRetries").GetInt32());
        var delays = retry.GetProperty("retryDelaysSeconds").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal([15.0, 90.0, 450.0], delays);

        var retention = json.GetProperty("retention");
        Assert.Equal(21, retention.GetProperty("logDeliveredRetentionDays").GetInt32());
        Assert.Equal(100, retention.GetProperty("logFailedRetentionDays").GetInt32());
        Assert.Equal(60, retention.GetProperty("spoolFailedRetentionDays").GetInt32());
        Assert.Equal(5, retention.GetProperty("capturedRetentionDays").GetInt32());
        Assert.Equal(8.25, retention.GetProperty("sizeTriggerGb").GetDouble());
        Assert.Equal(7.75, retention.GetProperty("sizeTargetGb").GetDouble());
    }

    [Fact]
    public async Task Config_returns_effective_listener_api_webui_shape()
    {
        var res = await host.Web.GetAsync("/api/config");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEmpty(json.GetProperty("listener").GetProperty("ports").EnumerateArray());
        Assert.NotEmpty(json.GetProperty("listener").GetProperty("allowedCidrs").EnumerateArray());
        // The test host configures the ingestion API port + rate limit.
        Assert.Equal(WebTestHost.ApiPort, json.GetProperty("api").GetProperty("port").GetInt32());
        Assert.Equal(100, json.GetProperty("api").GetProperty("rateLimitPerKey").GetInt32());
        Assert.True(json.GetProperty("webui").TryGetProperty("port", out _));
    }

    [Fact]
    public async Task Put_config_listener_persists_to_sql_and_refreshes_cache()
    {
        var put = await host.Web.PutAsJsonAsync("/api/config/listener", new
        {
            allowedCidrs = new[] { "10.0.0.0/8" },
            maxMessageBytes = 123456L,
            requireAuth = true,
        });
        put.EnsureSuccessStatusCode();

        var res = await host.Web.GetAsync("/api/config");
        res.EnsureSuccessStatusCode();
        var listener = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("listener");

        Assert.Equal(123456, listener.GetProperty("maxMessageBytes").GetInt64());
        Assert.True(listener.GetProperty("requireAuth").GetBoolean());
        Assert.Contains("10.0.0.0/8",
            listener.GetProperty("allowedCidrs").EnumerateArray().Select(e => e.GetString()));
    }
}
