using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Resend upstream relay via the Emails API (spec §8). Structured send (from/to/subject/html/text), Bearer
/// auth. Settings: ApiKey. Returns { id }.
/// </summary>
public sealed class ResendProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Resend";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var body = new Dictionary<string, object?>
        {
            ["from"] = ProviderHttp.From(message),
            ["to"] = ProviderHttp.Recipients(message),
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (html is not null) body["html"] = html;
        if (text is not null) body["text"] = text;
        if (html is null && text is null) body["text"] = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails") { Content = ProviderHttp.Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        string? id = null;
        try { if (JsonDocument.Parse(json).RootElement.TryGetProperty("id", out var m)) id = m.GetString(); }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} — Resend id: {id}");
    }
}
