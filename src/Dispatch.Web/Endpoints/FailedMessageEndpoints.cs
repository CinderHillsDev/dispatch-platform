using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MimeKit;

namespace Dispatch.Web.Endpoints;

/// <summary>
/// Failed-message management (spec §6.9): list, view, retry, and delete messages that exhausted retries
/// and landed in <c>spool/failed/</c>. Retry moves the file back to <c>incoming/</c> and wakes a worker.
/// </summary>
public static class FailedMessageEndpoints
{
    public static void MapFailedMessages(this RouteGroupBuilder group)
    {
        group.MapGet("/failed", (SpoolDirectory spool) =>
        {
            var dir = new DirectoryInfo(spool.FailedDir);
            if (!dir.Exists) return Results.Ok(Array.Empty<object>());

            var items = dir.EnumerateFiles("*.eml")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(500)
                .Select(f =>
                {
                    var meta = SpoolMeta.Peek(f.FullName);
                    string subject = "";
                    try { subject = MimeMessage.Load(f.FullName).Subject ?? ""; } catch { /* header parse best-effort */ }
                    return new
                    {
                        id = f.Name,
                        from = meta?.FromAddress ?? "",
                        to = meta?.ToAddresses ?? [],
                        subject,
                        retryCount = meta?.RetryCount ?? 0,
                        lastError = meta?.LastError,
                        ingestSource = meta?.IngestSource ?? "",
                        failedAt = f.LastWriteTimeUtc,
                        sizeBytes = f.Length,
                    };
                })
                .ToList();
            return Results.Ok(items);
        });

        group.MapGet("/failed/{id}", (string id, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();
            var meta = SpoolMeta.Peek(path);
            var msg = MimeMessage.Load(path);
            return Results.Ok(new
            {
                id,
                from = msg.From.ToString(),
                to = msg.To.ToString(),
                subject = msg.Subject ?? "",
                text = msg.TextBody,
                html = msg.HtmlBody,
                retryCount = meta?.RetryCount ?? 0,
                lastError = meta?.LastError,
            });
        });

        group.MapPost("/failed/{id}/retry", (string id, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();

            var meta = SpoolMeta.Peek(path) ?? new SpoolMeta();
            meta.RetryCount = 0;
            meta.NextRetryAt = null;
            meta.LastError = null;

            var destEml = Path.Combine(spool.IncomingDir, id);
            File.Move(path, destEml, overwrite: true);
            meta.Save(destEml);

            var oldMeta = SpoolMeta.PathFor(path);
            if (File.Exists(oldMeta)) File.Delete(oldMeta);

            spool.Signal(id);   // wake a worker to re-dispatch
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/failed/{id}", (string id, SpoolDirectory spool) =>
        {
            if (!TryResolve(spool, id, out var path)) return Results.NotFound();
            File.Delete(path);
            var meta = SpoolMeta.PathFor(path);
            if (File.Exists(meta)) File.Delete(meta);
            return Results.Ok(new { ok = true });
        });
    }

    private static bool TryResolve(SpoolDirectory spool, string id, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(id) || Path.GetFileName(id) != id || !id.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = Path.Combine(spool.FailedDir, id);
        if (!File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }
}
