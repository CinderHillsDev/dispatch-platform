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
        group.MapGet("/local/messages", (SpoolDirectory spool) =>
        {
            var dir = new DirectoryInfo(spool.CapturedDir);
            if (!dir.Exists) return Results.Ok(Array.Empty<object>());

            var items = dir.EnumerateFiles("*.eml")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(200)
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
            return Results.Ok(items);
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
            });
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
