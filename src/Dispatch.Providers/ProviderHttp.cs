using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Providers;
using MimeKit;

namespace Dispatch.Providers;

/// <summary>Shared helpers for the HTTP/JSON relay providers (Postmark, Resend, SparkPost, SMTP2GO).</summary>
internal static class ProviderHttp
{
    public static string From(RelayMessage m) => m.Message.From.Mailboxes.FirstOrDefault()?.Address ?? m.FromAddress;

    public static IReadOnlyList<string> Recipients(RelayMessage m) =>
        m.ToAddresses.Count > 0 ? m.ToAddresses : m.Message.To.Mailboxes.Select(x => x.Address).ToArray();

    /// <summary>To/Cc/Bcc recipients for structured-JSON providers. To/Cc are taken from the visible message
    /// headers (intersected with the actual envelope so we never deliver beyond it); Bcc is every envelope
    /// recipient not named in a visible header — so blind recipients are delivered without being exposed in
    /// the To/Cc the others see. Providers that post raw MIME (Mailgun, SES, SparkPost) don't need this — the
    /// envelope carries the real recipients and the MIME headers (which omit Bcc) are sent verbatim.</summary>
    public sealed record RecipientSet(IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc);

    public static RecipientSet SplitRecipients(RelayMessage m)
    {
        var envelope = m.ToAddresses.Count > 0
            ? m.ToAddresses
            : m.Message.To.Mailboxes.Select(x => x.Address).ToList();
        var envSet = new HashSet<string>(envelope, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var to = new List<string>();
        foreach (var a in m.Message.To.Mailboxes.Select(x => x.Address))
            if (envSet.Contains(a) && seen.Add(a)) to.Add(a);

        var cc = new List<string>();
        foreach (var a in m.Message.Cc.Mailboxes.Select(x => x.Address))
            if (envSet.Contains(a) && seen.Add(a)) cc.Add(a);

        var bcc = new List<string>();
        foreach (var a in envelope)
            if (seen.Add(a)) bcc.Add(a);

        // No visible recipients at all (header-less / undisclosed): treat the envelope as To so the send still
        // has a primary recipient rather than being bcc-only, which some provider APIs reject.
        if (to.Count == 0 && cc.Count == 0) return new RecipientSet(bcc, [], []);
        return new RecipientSet(to, cc, bcc);
    }

    public static string Subject(RelayMessage m) => m.Message.Subject ?? "";
    public static string? Html(RelayMessage m) => m.Message.HtmlBody;
    public static string? Text(RelayMessage m) => m.Message.TextBody;

    /// <summary>One attachment decoded for a structured-JSON send: filename, raw bytes, and content-type.</summary>
    public sealed record OutgoingAttachment(string FileName, byte[] Content, string ContentType)
    {
        public string Base64 => Convert.ToBase64String(Content);
    }

    /// <summary>Extracts the message's attachments (decoded) so structured-JSON providers can forward them.
    /// Providers that send raw MIME (SES, SparkPost) don't need this — attachments ride along in the MIME.</summary>
    public static IReadOnlyList<OutgoingAttachment> Attachments(RelayMessage m)
    {
        var list = new List<OutgoingAttachment>();
        foreach (var att in m.Message.Attachments)
        {
            if (att is not MimePart part || part.Content is null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            list.Add(new OutgoingAttachment(
                part.FileName ?? "attachment",
                ms.ToArray(),
                part.ContentType?.MimeType ?? "application/octet-stream"));
        }
        return list;
    }

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
