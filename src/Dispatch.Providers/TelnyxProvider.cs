using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Telnyx Email API upstream relay (spec §8). Structured JSON send to
/// <c>https://api.telnyx.com/v2/email_messages</c> with a Bearer API key; settings: ApiKey. Returns the
/// created message's <c>data.id</c> (a message UUID).
///
/// The send succeeds with <b>202 Accepted</b>, not 200: it means Telnyx queued the message, not that any
/// recipient received it. Delivery outcomes (delivered/bounced/complained) arrive through their
/// <c>email_*</c> webhooks or <c>GET /email_messages/{id}</c>, so a green Dispatch log entry for this provider
/// is an acceptance record. Errors use the standard Telnyx envelope (<c>errors[].code/title/detail</c>) with
/// normal HTTP status codes, so <see cref="ProviderHttp"/>'s mapping already fits their retry guidance:
/// 429/5xx back off and resend, other 4xx will fail identically until the request changes.
///
/// One shape difference from the other structured providers is worth noticing in a log: when *some* but not
/// all recipients are on Telnyx's suppression list, the send is still accepted and the dropped addresses come
/// back in a top-level <c>suppressed</c> array. That is a partial silent non-delivery, so it is surfaced in
/// the detail line rather than reported as a plain success. If every recipient is suppressed the API returns
/// a 4xx instead, which lands as a permanent failure through the normal path.
/// </summary>
public sealed class TelnyxProvider(RelayConfig config, HttpClient http) : IRelayProvider
{
    public string Name => "Telnyx";

    /// <summary>Their production base URL is <c>https://api.telnyx.com/v2</c>; the send endpoint is not
    /// region-selected, so unlike SES/SparkPost there is no Region setting to carry.</summary>
    internal const string Endpoint = "https://api.telnyx.com/v2/email_messages";

    /// <summary><c>Idempotency-Key</c> accepts 1-255 characters of letters, digits, '-' and '_'.</summary>
    private const int MaxIdempotencyKeyLength = 255;

    /// <summary>How many suppressed addresses to name in the log detail before falling back to a count.</summary>
    private const int MaxSuppressedListed = 5;

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var apiKey = ProviderHttp.Require(config, Name, "ApiKey");
        var html = ProviderHttp.Html(message);
        var text = ProviderHttp.Text(message);

        var rcpt = ProviderHttp.SplitRecipients(message);
        var from = message.Message.From.Mailboxes.FirstOrDefault();
        var fromAddress = ProviderHttp.From(message);

        var body = new Dictionary<string, object?>
        {
            // The address-object form rather than "Acme <a@b.com>", for two reasons: a display name carrying
            // a comma or an angle bracket (easy to get from mail reconstructed off an inbound message) makes
            // the string form ambiguous, and their schema exposes `from.name` as the structured equivalent.
            // A bare string is sent when there is no name, so nothing is added to what the recipient sees.
            ["from"] = string.IsNullOrWhiteSpace(from?.Name)
                ? fromAddress
                : new Dictionary<string, object?> { ["email"] = fromAddress, ["name"] = from.Name },
            ["to"] = rcpt.To,
            ["subject"] = ProviderHttp.Subject(message),
        };
        if (rcpt.Cc.Count > 0) body["cc"] = rcpt.Cc;
        if (rcpt.Bcc.Count > 0) body["bcc"] = rcpt.Bcc;
        if (html is not null) body["html_body"] = html;
        if (text is not null) body["text_body"] = text;
        if (html is null && text is null) body["text_body"] = "";

        // Reply-To is first-class here, but the schema is explicit that as an object only the email is stored
        // and the name is ignored. Sending the address alone says the same thing more honestly.
        var replyTo = message.Message.ReplyTo.Mailboxes.FirstOrDefault();
        if (replyTo is not null) body["reply_to"] = replyTo.Address;

        var attachments = ProviderHttp.Attachments(message);
        if (attachments.Count > 0)
            body["attachments"] = attachments.Select(a => new Dictionary<string, object?>
            {
                ["filename"] = a.FileName,
                ["content"] = a.Base64,
                ["content_type"] = a.ContentType,
            }).ToList();

        // Serialized here instead of through ProviderHttp.Json so the idempotency key commits to the exact
        // bytes on the wire, which is what a replay is deduplicated against.
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (IdempotencyKey(message.SpoolId, payload) is { } key)
            req.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        var (status, json) = await ProviderHttp.SendAsync(http, req, Name, ct);

        // Success shape: { "data": { "record_type": "email_message", "id": "<uuid>", ... },
        //                 "suppressed": [ { "to": "...", "reason": "..." } ] }.
        string? id = null;
        var suppressed = Array.Empty<string>();
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var i))
                id = i.GetString();
            suppressed = Suppressed(root);
            if (suppressed.Length == 0 && root.TryGetProperty("data", out var d2)) suppressed = Suppressed(d2);
        }
        catch
        {
            // A success body we cannot read is still a success; the id and the note are best-effort.
        }

        var detail = $"HTTP {status} - Telnyx id: {id}";
        if (suppressed.Length > 0)
            detail += suppressed.Length > MaxSuppressedListed
                ? $" - {suppressed.Length} recipient(s) suppressed by Telnyx and NOT sent"
                : $" - suppressed (NOT sent): {string.Join(", ", suppressed)}";

        return RelayResult.Success(id, detail);
    }

    /// <summary>Suppressed recipients as "address (reason)" fragments, from either response location.</summary>
    private static string[] Suppressed(JsonElement root) =>
        root.TryGetProperty("suppressed", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray()
                .Select(e =>
                {
                    var addr = e.TryGetProperty("to", out var t) ? t.GetString() : null;
                    var reason = e.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    return string.IsNullOrEmpty(addr) ? null : $"{addr}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}";
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// A key Telnyx can dedupe on: the spool id (which names exactly one message) qualified by a digest of the
    /// request body. The digest is what makes this safe rather than clever. Their docs bind a replay to "the
    /// same request" and, unlike some peers, do not document that a failed send releases the key - so a key
    /// derived from the spool id alone could either replay a stored failure after an operator fixed something,
    /// or replay a stored success for content that has since changed. With the digest, a byte-identical retry
    /// (the case worth protecting: the worker saw a transport error, the send actually happened, a retry would
    /// deliver twice) replays, and any change to the message produces a fresh key and really sends.
    /// Returns null when there is no spool identity to key by, so such a message is sent without protection
    /// rather than behind a made-up key every other message would collide with.
    /// </summary>
    private static string? IdempotencyKey(string? spoolId, string payload)
    {
        if (string.IsNullOrWhiteSpace(spoolId)) return null;

        var chars = spoolId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray();
        if (chars.Length == 0) return null;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..24];
        var key = new string(chars);
        var budget = MaxIdempotencyKeyLength - digest.Length - 1;
        if (key.Length > budget) key = key[..budget];
        return $"{key}-{digest}";
    }
}
