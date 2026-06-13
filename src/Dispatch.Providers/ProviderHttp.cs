using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>Shared helpers for the HTTP/JSON relay providers (Postmark, Resend, SparkPost, SMTP2GO).</summary>
internal static class ProviderHttp
{
    public static string From(RelayMessage m) => m.Message.From.Mailboxes.FirstOrDefault()?.Address ?? m.FromAddress;

    public static IReadOnlyList<string> Recipients(RelayMessage m) =>
        m.ToAddresses.Count > 0 ? m.ToAddresses : m.Message.To.Mailboxes.Select(x => x.Address).ToArray();

    public static string Subject(RelayMessage m) => m.Message.Subject ?? "";
    public static string? Html(RelayMessage m) => m.Message.HtmlBody;
    public static string? Text(RelayMessage m) => m.Message.TextBody;

    public static async Task<string> RawMimeAsync(RelayMessage m, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await m.Message.WriteToAsync(ms, ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static HttpContent Json(object body) =>
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>Sends the request, mapping transport errors and 429/5xx to <see cref="TransientRelayException"/>
    /// and other non-2xx to a permanent <see cref="InvalidOperationException"/>. Returns (status, body).</summary>
    public static async Task<(int Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage req, string provider, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new TransientRelayException($"{provider} request failed: {ex.Message}", ex);
        }

        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode) return ((int)res.StatusCode, body);

        var detail = $"{provider} {(int)res.StatusCode}: {body}";
        if ((int)res.StatusCode == 429 || ((int)res.StatusCode >= 500 && (int)res.StatusCode < 600))
            throw new TransientRelayException(detail);
        throw new InvalidOperationException(detail);
    }

    public static string? Setting(RelayConfig c, string key) => c.Settings.TryGetValue(key, out var v) ? v : null;

    public static string Require(RelayConfig c, string provider, string key)
    {
        var v = Setting(c, key);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"{provider} relay '{key}' is not configured.");
        return v;
    }
}
