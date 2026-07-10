using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MimeKit;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Local Inbox: lists and views the messages captured by the Local/developer provider in
/// <c>spool/captured/</c> so developers can inspect simulated mail flow without sending externally.
/// </summary>
public static class LocalInboxEndpoints
{
    public static void MapLocalInbox(this RouteGroupBuilder group)
    {
        group.MapGet("/local/messages", (SpoolDirectory spool, HttpContext ctx) =>
        {
            var dir = new DirectoryInfo(spool.CapturedDir);
            if (!dir.Exists) return Results.Ok(new { items = Array.Empty<object>(), total = 0, page = 1, pageSize = 50 });

            var q = ctx.Request.Query;
            var pageSize = Math.Clamp(int.TryParse(q["pageSize"], out var ps) ? ps : 50, 1, 200);
            var page = Math.Max(1, int.TryParse(q["page"], out var pg) ? pg : 1);

            var all = dir.EnumerateFiles("*.eml").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var items = all
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(f =>
                {
                    try
                    {
                        var msg = MimeMessage.Load(f.FullName);
                        return new
                        {
                            id = f.Name,
                            from = msg.From.ToString(),
                            to = msg.To.ToString(),
                            subject = msg.Subject ?? "",
                            date = f.LastWriteTimeUtc,
                            sizeBytes = f.Length,
                        };
                    }
                    catch
                    {
                        return new { id = f.Name, from = "", to = "", subject = "(unparseable)", date = f.LastWriteTimeUtc, sizeBytes = f.Length };
                    }
                })
                .ToList();
            return Results.Ok(new { items, total = all.Count, page, pageSize });
        });

        group.MapGet("/local/messages/{id}", (string id, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();
            var msg = MimeMessage.Load(path);
            return Results.Ok(new
            {
                id,
                from = msg.From.ToString(),
                to = msg.To.ToString(),
                cc = msg.Cc.ToString(),
                subject = msg.Subject ?? "",
                date = msg.Date.UtcDateTime,
                text = msg.TextBody,
                html = msg.HtmlBody,
                attachments = msg.Attachments.Select((a, i) => new
                {
                    index = i,
                    name = AttachmentName(a, i),
                    contentType = a.ContentType?.MimeType ?? "application/octet-stream",
                    sizeBytes = AttachmentBytes(a)?.Length ?? 0,
                }).ToArray(),
            });
        });

        // Download a single attachment (decoded) from a captured message.
        group.MapGet("/local/messages/{id}/attachments/{index:int}", (string id, int index, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();
            var msg = MimeMessage.Load(path);
            var att = msg.Attachments.ElementAtOrDefault(index);
            if (att is null) return Results.NotFound();
            var bytes = AttachmentBytes(att);
            if (bytes is null) return Results.NotFound();
            return Results.File(bytes, att.ContentType?.MimeType ?? "application/octet-stream", AttachmentName(att, index));
        });

        group.MapDelete("/local/messages/{id}", (string id, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();
            File.Delete(path);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/local/messages", (SpoolDirectory spool) =>
        {
            if (Directory.Exists(spool.CapturedDir))
                foreach (var f in Directory.EnumerateFiles(spool.CapturedDir, "*.eml"))
                    File.Delete(f);
            return Results.Ok(new { ok = true });
        });
    }

    private static string AttachmentName(MimeEntity entity, int index) =>
        entity.ContentDisposition?.FileName
        ?? entity.ContentType?.Name
        ?? $"attachment-{index + 1}";

    // Decoded bytes of an attachment (null for non-part entities, e.g. embedded message/rfc822, or a part
    // with no content).
    private static byte[]? AttachmentBytes(MimeEntity entity)
    {
        if (entity is not MimePart part || part.Content is null) return null;
        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        return ms.ToArray();
    }

    // Guards against path traversal: the id must be a bare .eml filename inside the captured dir.
    private static bool TryResolve(SpoolDirectory spool, string id, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(id) || Path.GetFileName(id) != id || !id.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = Path.Combine(spool.CapturedDir, id);
        if (!File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }
}
