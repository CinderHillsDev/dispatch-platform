using System.Net.Http;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// SMTP2GO upstream relay via the v3 email/send API (spec §8). Structured send (sender/to/subject/bodies);
/// the API key travels in the JSON body. Settings: ApiKey. Returns data.email_id.
/// </summary>
public sealed class Smtp2GoProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "SMTP2GO";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var body = new Dictionary<string, object?>
        {
            ["api_key"] = apiKey,
            ["sender"] = ProviderHttp.From(message),
            ["to"] = ProviderHttp.Recipients(message),
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (html is not null) body["html_body"] = html;
        if (text is not null) body["text_body"] = text;
        if (html is null && text is null) body["text_body"] = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.smtp2go.com/v3/email/send") { Content = ProviderHttp.Json(body) };

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        string? id = null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("data", out var d) && d.TryGetProperty("email_id", out var m)) id = m.GetString();
        }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} — SMTP2GO email_id: {id}");
    }
}
