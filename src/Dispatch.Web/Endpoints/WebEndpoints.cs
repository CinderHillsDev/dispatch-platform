using System.Diagnostics;
using Dispatch.Core.ApiKeys;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Spool;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MimeKit;

namespace Dispatch.Web.Endpoints;

public static class WebEndpoints
{
    /// <summary>Ingestion API (spec §7), reachable only on the API port; auth is enforced by middleware.</summary>
    public static void MapIngestionApi(this IEndpointRouteBuilder app, int apiPort)
    {
        var group = app.MapGroup("/api/v1").RequireLocalPort(apiPort);

        group.MapPost("/messages", (HttpContext ctx, ApiMessageHandler handler, CancellationToken ct) =>
            handler.HandleAsync(ctx, ct));

        group.MapGet("/messages/{id}", async (string id, SpoolDirectory spool, IMessageLogQuery logs, CancellationToken ct) =>
        {
            var raw = id.StartsWith("spl_", StringComparison.OrdinalIgnoreCase) ? id[4..] : id;
            if (!Guid.TryParse(raw, out var guid))
                return Results.BadRequest(new { error = "Invalid message id." });

            var file = $"{guid}.eml";
            if (File.Exists(Path.Combine(spool.IncomingDir, file))) return Status(id, "queued");
            if (File.Exists(Path.Combine(spool.ProcessingDir, file))) return Status(id, "processing");
            if (File.Exists(Path.Combine(spool.FailedDir, file))) return Status(id, "failed");

            var row = await logs.GetBySpoolIdAsync(guid.ToString(), ct);
            if (row is null) return Results.NotFound(new { id, status = "unknown" });

            var status = row.Event switch
            {
                "Delivered" => "delivered",
                "Retrying" => "retrying",
                "Failed" => "failed",
                _ => row.Event.ToLowerInvariant(),
            };
            return Results.Ok(new { id, status, provider = row.Provider, deliveredAt = row.LoggedAt, durationMs = row.DurationMs });
        });

        static IResult Status(string id, string status) => Results.Ok(new { id, status });
    }

    /// <summary>Dashboard read/stats API + admin, reachable only on the web port (spec §9.2).</summary>
    public static void MapDashboardApi(this IEndpointRouteBuilder app, int webPort)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));   // no auth, any port (§14)

        var group = app.MapGroup("/api").RequireLocalPort(webPort);

        group.MapGet("/stats", async (ICounterReader counters, SpoolDirectory spool, CancellationToken ct) =>
        {
            var totals = await counters.GetTodayAsync(ct);
            return Results.Ok(new
            {
                totals.Received, totals.Delivered, totals.Failed, totals.Retried, totals.Denied,
                spool = SpoolCounts(spool),
            });
        });

        group.MapGet("/stats/throughput", (MinuteCounterRing ring) => Results.Ok(ring.Snapshot()));

        group.MapGet("/spool", (SpoolDirectory spool) => Results.Ok(SpoolCounts(spool)));

        group.MapGet("/messages", async (HttpContext ctx, IMessageLogQuery logs, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var filter = new MessageLogFilter
            {
                FromUtc = ParseDate(q["from"]),
                ToUtc = ParseDate(q["to"]),
                Statuses = q["status"].Count > 0
                    ? q["status"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : null,
                IngestSource = NullIfEmpty(q["source"].ToString()),
                FromDomain = NullIfEmpty(q["fromDomain"].ToString()),
                ToDomain = NullIfEmpty(q["toDomain"].ToString()),
                Limit = int.TryParse(q["limit"], out var l) ? l : 50,
                Cursor = ParseDate(q["cursorAt"]) is { } at && long.TryParse(q["cursorId"], out var cid)
                    ? new MessageLogCursor(at, cid)
                    : null,
            };
            var page = await logs.QueryAsync(filter, ct);
            return Results.Ok(new
            {
                rows = page.Rows,
                nextCursor = page.NextCursor is { } c ? new { at = c.LoggedAt, id = c.Id } : null,
            });
        });

        group.MapRelayRouting();   // /api/relays/* and /api/routing/* (see RoutingEndpoints)

        group.MapGet("/keys", async (IApiKeyRepository keys, CancellationToken ct) =>
            Results.Ok((await keys.ListAsync(includeRevoked: true, ct)).Select(Public)));

        group.MapPost("/keys", async (CreateKeyRequest req, IApiKeyRepository keys, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            var created = await keys.CreateAsync(req.Name, req.RateLimitPerMinute ?? 0, ct);
            return Results.Ok(new
            {
                created.Key.Id, created.Key.KeyId, created.Key.Name,
                created.Key.RateLimitPerMinute, key = created.PlaintextKey,
            });
        });

        group.MapDelete("/keys/{id:int}", async (int id, IApiKeyRepository keys, CancellationToken ct) =>
            await keys.RevokeAsync(id, ct) ? Results.Ok() : Results.NotFound());
    }

    private static object SpoolCounts(SpoolDirectory spool) => new
    {
        incoming = Directory.EnumerateFiles(spool.IncomingDir, "*.eml").Count(),
        processing = Directory.EnumerateFiles(spool.ProcessingDir, "*.eml").Count(),
        failed = Directory.EnumerateFiles(spool.FailedDir, "*.eml").Count(),
    };

    private static object Public(ApiKey k) => new
    {
        k.Id, k.KeyId, k.Name, k.CreatedAt, k.LastUsedAt, k.MessageCount, k.Revoked, k.RateLimitPerMinute,
    };

    private static RouteGroupBuilder RequireLocalPort(this RouteGroupBuilder group, int port)
    {
        group.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Connection.LocalPort == port ? await next(ctx) : Results.NotFound());
        return group;
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "";
    }

    internal static RelayLogEntry TestEntry(
        string status, int relayId, string relayName, string provider, RelayMessage msg,
        string? subject, int durationMs, string? providerMessageId, string? detail, string? error) => new()
    {
        Event = "TestSent",
        Status = status,
        SpoolId = "test-" + Guid.NewGuid().ToString("N")[..8],
        FromAddress = msg.FromAddress,
        FromDomain = Domain(msg.FromAddress),
        ToAddresses = msg.ToAddresses,
        ToDomain = Domain(msg.ToAddresses.FirstOrDefault() ?? ""),
        Subject = subject,
        RelayId = relayId,
        RelayName = relayName,
        Provider = provider,
        ProviderMessageId = providerMessageId,
        ProviderResponse = detail,
        Error = error,
        DurationMs = durationMs,
        IngestSource = "Test",
    };

    public sealed record CreateKeyRequest(string Name, int? RateLimitPerMinute);
    public sealed record TestRelayRequest(string To, string? From);
}
