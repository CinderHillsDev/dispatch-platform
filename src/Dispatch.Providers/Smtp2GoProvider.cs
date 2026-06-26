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

        var rcpt = ProviderHttp.SplitRecipients(message);
        var body = new Dictionary<string, object?>
        {
            ["api_key"] = apiKey,
            ["sender"] = ProviderHttp.From(message),
            ["to"] = rcpt.To,
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (rcpt.Cc.Count > 0) body["cc"] = rcpt.Cc;
        if (rcpt.Bcc.Count > 0) body["bcc"] = rcpt.Bcc;
        if (html is not null) body["html_body"] = html;
        if (text is not null) body["text_body"] = text;
        if (html is null && text is null) body["text_body"] = "";

        var attachments = ProviderHttp.Attachments(message);
        if (attachments.Count > 0)
            body["attachments"] = attachments.Select(a => new Dictionary<string, object?>
            {
                ["filename"] = a.FileName,
                ["fileblob"] = a.Base64,
                ["mimetype"] = a.ContentType,
            }).ToList();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.smtp2go.com/v3/email/send") { Content = ProviderHttp.Json(body) };

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        // SMTP2GO returns HTTP 200 even when it rejects the message — the real outcome lives in `data`
        // (succeeded/failed counts, or an error/field_validation_errors). Treat anything that isn't a
        // confirmed success as a permanent failure so it's logged and surfaced rather than silently "sent".
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SMTP2GO returned an unexpected response (HTTP {status}): {json}");

        if (data.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
        {
            var code = data.TryGetProperty("error_code", out var ec) ? ec.GetString() : null;
            var fields = data.TryGetProperty("field_validation_errors", out var fve) ? fve.GetRawText() : null;
            throw new InvalidOperationException(
                $"SMTP2GO rejected the message: {err.GetString()}" +
                (code is not null ? $" (code {code})" : "") +
                (fields is not null ? $" — {fields}" : ""));
        }

        var succeeded = data.TryGetProperty("succeeded", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
        if (succeeded < 1)
        {
            var failures = data.TryGetProperty("failures", out var f) && f.ValueKind == JsonValueKind.Array
                ? string.Join("; ", f.EnumerateArray().Select(x => x.ToString()))
                : null;
            throw new InvalidOperationException(
                $"SMTP2GO did not accept the message (succeeded=0){(string.IsNullOrWhiteSpace(failures) ? $": {json}" : $": {failures}")}");
        }

        string? id = data.TryGetProperty("email_id", out var m) ? m.GetString() : null;
        return RelayResult.Success(id, $"HTTP {status} — SMTP2GO succeeded={succeeded}, email_id: {id}");
    }
}
