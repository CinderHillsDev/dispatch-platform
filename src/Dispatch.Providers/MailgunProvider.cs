using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Mailgun upstream relay via the Messages API (spec §8.1). Posts the raw MIME to <c>/messages.mime</c>
/// so attachments and headers are preserved exactly. Settings: ApiKey, Domain, Region (US|EU).
/// 4xx-except-auth and 5xx/429 map to <see cref="TransientRelayException"/>.
/// </summary>
public sealed class MailgunProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Mailgun";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = Require("ApiKey");
        var domain = Require("Domain");
        var region = (Setting("Region") ?? "US").Trim().ToUpperInvariant();
        var baseUrl = region == "EU" ? "https://api.eu.mailgun.net" : "https://api.mailgun.net";

        byte[] mime;
        using (var ms = new MemoryStream())
        {
            await message.Message.WriteToAsync(ms, ct);
            mime = ms.ToArray();
        }

        using var content = new MultipartFormDataContent();
        foreach (var recipient in message.ToAddresses)
            content.Add(new StringContent(recipient), "to");

        var mimePart = new ByteArrayContent(mime);
        mimePart.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        content.Add(mimePart, "message", "message.mime");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/{domain}/messages.mime")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientRelayException($"Mailgun request failed: {ex.Message}", ex);
        }

        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = $"Mailgun {(int)response.StatusCode}: {bodyText}";
            if (IsTransient(response.StatusCode))
                throw new TransientRelayException(detail);
            throw new InvalidOperationException(detail);
        }

        string? id = null;
        try { id = JsonDocument.Parse(bodyText).RootElement.GetProperty("id").GetString(); }
        catch { /* id is best-effort */ }

        return RelayResult.Success(id, bodyText);
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code is 429 or >= 500 && (int)code < 600;

    private string? Setting(string key) => config.Settings.TryGetValue(key, out var v) ? v : null;

    private string Require(string key)
    {
        var v = Setting(key);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Mailgun relay '{key}' is not configured.");
        return v;
    }
}
