using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>
/// Maileroo upstream relay via the Email API v2 (spec §8): structured JSON send with Bearer auth.
/// from/to/cc are {address, display_name} objects; bodies are html/plain; attachments are base64.
/// Settings: ApiKey (sending key). Returns { data.reference_id }.
/// </summary>
public sealed class MailerooProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Maileroo";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var fromMb = message.Message.From.Mailboxes.FirstOrDefault();
        var body = new Dictionary<string, object?>
        {
            ["from"] = Address(fromMb?.Address ?? message.FromAddress, fromMb?.Name),
            ["to"] = Recipients(message),
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (html is not null) body["html"] = html;
        if (text is not null) body["plain"] = text;
        if (html is null && text is null) body["plain"] = "";

        var attachments = Attachments(message);
        if (attachments.Count > 0) body["attachments"] = attachments;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://smtp.maileroo.com/api/v2/emails")
        {
            Content = ProviderHttp.Json(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        // Success shape: { "success": true, "data": { "reference_id": "..." } }
        string? id = null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("data", out var d) && d.TryGetProperty("reference_id", out var r))
                id = r.GetString();
        }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} — Maileroo reference: {id}");
    }

    private static Dictionary<string, object?> Address(string addr, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? new Dictionary<string, object?> { ["address"] = addr }
            : new Dictionary<string, object?> { ["address"] = addr, ["display_name"] = name };

    private static List<object> Recipients(RelayMessage m)
    {
        var mboxes = m.Message.To.Mailboxes.ToList();
        if (mboxes.Count > 0) return mboxes.Select(x => (object)Address(x.Address, x.Name)).ToList();
        return m.ToAddresses.Select(a => (object)Address(a, null)).ToList();
    }

    private static List<object> Attachments(RelayMessage m)
    {
        var list = new List<object>();
        foreach (var att in m.Message.Attachments)
        {
            if (att is not MimePart part) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            list.Add(new Dictionary<string, object?>
            {
                ["file_name"] = part.FileName ?? "attachment",
                ["content"] = Convert.ToBase64String(ms.ToArray()),
                ["content_type"] = part.ContentType?.MimeType ?? "application/octet-stream",
            });
        }
        return list;
    }
}
