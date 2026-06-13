using Dispatch.Core.Configuration;
using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MimeKit;

namespace Dispatch.Web.Ingestion;

/// <summary>
/// Turns an HTTP <c>POST /api/v1/messages</c> (multipart or JSON, spec §7.4–§7.5) into a spool entry:
/// builds a <see cref="MimeMessage"/>, writes the RFC5322 <c>.eml</c> plus its <c>.meta</c> sidecar to
/// <c>spool/incoming/</c> (so the worker can claim it exactly as an SMTP message), and returns 202.
/// </summary>
public sealed class ApiMessageHandler(SpoolDirectory spool, IOptions<ApiOptions> apiOptions, ILogger<ApiMessageHandler> log)
{
    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        var maxBytes = apiOptions.Value.MaxMessageBytes;

        // Reject oversized uploads early when the client declares Content-Length.
        if (maxBytes > 0 && ctx.Request.ContentLength is { } declared && declared > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        MimeMessage mime;
        string[] tags;
        try
        {
            (mime, tags) = ctx.Request.HasFormContentType
                ? await BuildFromFormAsync(ctx, ct)
                : await BuildFromJsonAsync(ctx, ct);
        }
        catch (ApiValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is FormatException or ParseException or System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = $"Invalid request: {ex.Message}" });
        }

        var from = mime.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var to = mime.To.Mailboxes.Select(m => m.Address).ToArray();

        var id = Guid.NewGuid();
        var emlPath = spool.IncomingPath(id);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await mime.WriteToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        // Enforce the size ceiling on the serialized message (catches uploads without Content-Length).
        if (maxBytes > 0 && bytes.Length > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        await File.WriteAllBytesAsync(emlPath, bytes, ct);

        new SpoolMeta
        {
            SpoolId = id,
            ReceivedAt = DateTime.UtcNow,
            FromAddress = from,
            ToAddresses = to,
            IngestSource = "API",
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            Tags = tags.Length > 0 ? tags : null,
        }.Save(emlPath);

        spool.Signal(Path.GetFileName(emlPath));
        log.LogInformation("Accepted API message {SpoolId} from {From} ({Size} bytes) → 202", id, from, bytes.Length);

        var spl = $"spl_{id:N}";
        return Results.Accepted($"/api/v1/messages/{spl}", new { id = spl, message = "Queued. Thank you." });
    }

    private static async Task<(MimeMessage, string[])> BuildFromJsonAsync(HttpContext ctx, CancellationToken ct)
    {
        var req = await ctx.Request.ReadFromJsonAsync<SendMessageRequest>(ct)
                  ?? throw new ApiValidationException("Request body is empty.");

        Require(req.From, "from");
        if (req.To is not { Length: > 0 }) throw new ApiValidationException("At least one 'to' recipient is required.");
        Require(req.Subject, "subject");
        if (string.IsNullOrEmpty(req.Text) && string.IsNullOrEmpty(req.Html))
            throw new ApiValidationException("At least one of 'text' or 'html' is required.");

        var mime = BuildMime(req.From!, req.To, req.Cc, req.Bcc, req.Subject!, req.Text, req.Html, req.Headers, attachments: null);
        return (mime, req.Tags ?? []);
    }

    private static async Task<(MimeMessage, string[])> BuildFromFormAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);

        var from = form["from"].ToString();
        Require(from, "from");
        var to = Flatten(form["to"]);
        if (to.Length == 0) throw new ApiValidationException("At least one 'to' recipient is required.");
        var subject = form["subject"].ToString();
        Require(subject, "subject");

        var text = NullIfEmpty(form["text"].ToString());
        var html = NullIfEmpty(form["html"].ToString());
        if (text is null && html is null) throw new ApiValidationException("At least one of 'text' or 'html' is required.");

        var headers = form.Keys
            .Where(k => k.StartsWith("h:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(k => k[2..], k => form[k].ToString());

        var attachments = form.Files
            .Where(f => string.Equals(f.Name, "attachment", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var mime = BuildMime(from, to, Flatten(form["cc"]), Flatten(form["bcc"]),
            subject, text, html, headers, attachments);

        var tags = Flatten(form["o:tag"]);
        return (mime, tags);
    }

    private static MimeMessage BuildMime(
        string from, string[] to, string[]? cc, string[]? bcc, string subject,
        string? text, string? html, IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<IFormFile>? attachments)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        foreach (var t in to) msg.To.AddRange(InternetAddressList.Parse(t));
        if (cc is not null) foreach (var c in cc) msg.Cc.AddRange(InternetAddressList.Parse(c));
        if (bcc is not null) foreach (var b in bcc) msg.Bcc.AddRange(InternetAddressList.Parse(b));
        msg.Subject = subject;

        var body = new BodyBuilder { TextBody = text, HtmlBody = html };
        if (attachments is not null)
        {
            foreach (var file in attachments)
            {
                using var s = file.OpenReadStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                body.Attachments.Add(file.FileName, ms.ToArray());
            }
        }
        msg.Body = body.ToMessageBody();

        if (headers is not null)
            foreach (var (name, value) in headers)
                msg.Headers.Add(name, value);

        return msg;
    }

    private static string[] Flatten(StringValues values) =>
        values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .ToArray();

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ApiValidationException($"'{field}' is required.");
    }

    private sealed class ApiValidationException(string message) : Exception(message);
}
