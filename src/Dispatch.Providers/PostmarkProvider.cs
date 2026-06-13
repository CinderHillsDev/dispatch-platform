using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Postmark upstream relay via the Email API (spec §8). Sends a structured email (from/to/subject/bodies).
/// Settings: ApiKey (server token), optional MessageStream (default "outbound"). Postmark returns 200 with an
/// ErrorCode — a non-zero ErrorCode is a permanent failure even on HTTP 200.
/// </summary>
public sealed class PostmarkProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Postmark";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var token = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var body = new Dictionary<string, object?>
        {
            ["From"] = ProviderHttp.From(message),
            ["To"] = string.Join(",", ProviderHttp.Recipients(message)),
            ["Subject"] = ProviderHttp.Subject(message),
            ["MessageStream"] = ProviderHttp.Setting(config, "MessageStream") ?? "outbound",
        };
        if (html is not null) body["HtmlBody"] = html;
        if (text is not null) body["TextBody"] = text;
        if (html is null && text is null) body["TextBody"] = "";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.postmarkapp.com/email") { Content = ProviderHttp.Json(body) };
        req.Headers.Add("X-Postmark-Server-Token", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        string? id = null; var errorCode = 0; string? msg = null;
        try
        {
            var r = JsonDocument.Parse(json).RootElement;
            if (r.TryGetProperty("MessageID", out var m)) id = m.GetString();
            if (r.TryGetProperty("ErrorCode", out var e)) errorCode = e.GetInt32();
            if (r.TryGetProperty("Message", out var mm)) msg = mm.GetString();
        }
        catch { /* best-effort parse */ }

        if (errorCode != 0) throw new InvalidOperationException($"Postmark ErrorCode {errorCode}: {msg}");
        return RelayResult.Success(id, $"HTTP {status} — Postmark MessageID: {id}");
    }
}
