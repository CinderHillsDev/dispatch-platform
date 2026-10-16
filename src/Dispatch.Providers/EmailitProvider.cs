using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Emailit upstream relay via the v2 Emails API (spec §8). Structured send to
/// https://api.emailit.com/v2/emails with Bearer auth; keys are prefixed <c>secret_</c>. Settings: ApiKey.
/// Returns the created email object's <c>id</c> (<c>em_...</c>).
///
/// Semantics worth knowing when reading a Dispatch log entry: a 200 means Emailit *accepted and queued* the
/// message, not that it was delivered - <c>status</c> is <c>accepted</c> or <c>scheduled</c> (a future-dated
/// send is a success here), and delivery outcomes arrive via their webhooks. Errors use normal HTTP status
/// codes, so <see cref="ProviderHttp"/>'s mapping already fits their documented retry advice: 429/500/503
/// retry with backoff, every other 4xx is a request problem that will fail identically until it is fixed.
///
/// Two provider limits shape what we can send, and neither is enforced here because the API answers clearly
/// and the failure is logged once rather than retried forever: at most 50 recipients in each of to/cc/bcc,
/// and attachments must carry an extension on Emailit's allow list (a .exe comes back as a 400).
///
/// Unlike the other structured providers this one sends an <c>Idempotency-Key</c>, derived from the spool id.
/// A lost response is the awkward case for a relay: the worker sees a transport error, marks the message
/// transient, retries the same message, and the recipient gets a duplicate. Their documented replay window
/// closes that hole for free - a repeat within 24h replays the stored success without sending or spending a
/// credit. What makes it safe rather than clever is that *failures release the key*, so fixing the request and
/// retrying does send, and a spool id names exactly one message, which is the one reuse their docs warn about.
/// </summary>
public sealed class EmailitProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Emailit";

    /// <summary>Emailit accepts 1-256 characters of letters, digits, '-' and '_'.</summary>
    private const int MaxIdempotencyKeyLength = 256;

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var rcpt = ProviderHttp.SplitRecipients(message);
        var fromMb = message.Message.From.Mailboxes.FirstOrDefault();
        var body = new Dictionary<string, object?>
        {
            // Their from takes the "Acme <hello@acme.com>" form, so the display name survives. Sending the bare
            // address would silently rewrite what the recipient sees, which a relay should not do.
            ["from"] = Display(ProviderHttp.From(message), fromMb?.Name),
            ["to"] = rcpt.To,
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (rcpt.Cc.Count > 0) body["cc"] = rcpt.Cc;
        if (rcpt.Bcc.Count > 0) body["bcc"] = rcpt.Bcc;
        if (html is not null) body["html"] = html;
        if (text is not null) body["text"] = text;
        if (html is null && text is null) body["text"] = "";

        // Reply-To is a first-class field here, and a reconstructed send that drops it sends replies to the
        // envelope sender - often a bounce-only address - instead of the person who actually wrote the mail.
        var replyTo = message.Message.ReplyTo.Mailboxes.ToList();
        if (replyTo.Count > 0) body["reply_to"] = replyTo.Select(m => Display(m.Address, m.Name)).ToList();

        var attachments = ProviderHttp.Attachments(message);
        if (attachments.Count > 0)
            body["attachments"] = attachments.Select(a => new Dictionary<string, object?>
            {
                ["filename"] = a.FileName,
                ["content"] = a.Base64,          // base64 inline; their url form would need a public fetch
                ["content_type"] = a.ContentType,
            }).ToList();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.emailit.com/v2/emails")
        {
            Content = ProviderHttp.Json(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (IdempotencyKey(message.SpoolId) is { } key) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        // Success shape: { "object": "email", "id": "em_...", "status": "accepted", "ids": { ... } }.
        // id is the send's identifier (the first recipient's when there are several), which is what we log.
        string? id = null;
        try { if (JsonDocument.Parse(json).RootElement.TryGetProperty("id", out var m)) id = m.GetString(); }
        catch { /* best-effort */ }

        return RelayResult.Success(id, $"HTTP {status} - Emailit id: {id}");
    }

    /// <summary>The spool id as a key Emailit will dedupe on, or null if there is nothing usable to key by
    /// (a message with no spool identity is sent without protection rather than with a made-up key).</summary>
    private static string? IdempotencyKey(string? spoolId)
    {
        if (string.IsNullOrWhiteSpace(spoolId)) return null;
        var chars = spoolId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(MaxIdempotencyKeyLength).ToArray();
        return chars.Length > 0 ? new string(chars) : null;
    }

    private static string Display(string address, string? name) =>
        string.IsNullOrWhiteSpace(name) ? address : $"{name} <{address}>";
}
