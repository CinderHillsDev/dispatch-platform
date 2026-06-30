using System.Net.Http;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// SparkPost upstream relay via the Transmissions API (spec §8). Posts the raw MIME (<c>email_rfc822</c>) so
/// attachments + headers are preserved. Settings: ApiKey, optional Region (US|EU). Auth via the API key header.
/// </summary>
public sealed class SparkPostProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "SparkPost";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var region = (ProviderHttp.Setting(config, "Region") ?? "US").Trim().ToUpperInvariant();
        var baseUrl = region == "EU" ? "https://api.eu.sparkpost.com" : "https://api.sparkpost.com";
        var mime = await ProviderHttp.RawMimeAsync(message, ct);

        var body = new Dictionary<string, object?>
        {
            ["recipients"] = ProviderHttp.Recipients(message).Select(r => new { address = r }).ToArray(),
            ["content"] = new Dictionary<string, object?> { ["email_rfc822"] = mime },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/transmissions") { Content = ProviderHttp.Json(body) };
        req.Headers.Add("Authorization", apiKey);

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        string? id = null;
        try { if (JsonDocument.Parse(json).RootElement.TryGetProperty("results", out var r) && r.TryGetProperty("id", out var m)) id = m.GetString(); }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} - SparkPost id: {id}");
    }
}
