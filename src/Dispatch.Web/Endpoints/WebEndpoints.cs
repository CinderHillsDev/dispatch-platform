using System.Diagnostics;
using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Spool;
using Dispatch.Web.Auth;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Dispatch.Web.Endpoints;

public static class WebEndpoints
{
    private static readonly DateTime ProcessStartUtc =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly string Version =
        typeof(WebEndpoints).Assembly.GetName().Version?.ToString() ?? "dev";

    /// <summary>Ingestion API (spec §7), reachable only on the API port; auth is enforced by middleware.</summary>
    public static void MapIngestionApi(this IEndpointRouteBuilder app, int apiPort)
    {
        var group = app.MapGroup("/api/v1").RequireLocalPort(apiPort);

        group.MapPost("/messages", (HttpContext ctx, ApiMessageHandler handler, CancellationToken ct) =>
            handler.HandleAsync(ctx, ct));

        // Per-key recent message list (spec §7.4). Scoped to the calling key — never leaks other keys' messages.
        group.MapGet("/messages", async (HttpContext ctx, IMessageLogQuery logs, CancellationToken ct) =>
        {
            if (ctx.Items[ApiKeyMiddleware.ApiKeyItem] is not ApiKey key)
                return Results.Unauthorized();

            var q = ctx.Request.Query;
            var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 200) : 20;
            var statuses = q["status"].Count > 0
                ? q["status"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;

            var rows = await logs.RecentByApiKeyAsync(key.Id, limit, statuses, ct);
            return Results.Ok(new { messages = rows });
        });

        group.MapGet("/messages/{id}", async (string id, HttpContext ctx, SpoolDirectory spool, IMessageLogQuery logs, CancellationToken ct) =>
        {
            // Scope to the calling key so one key can't probe another key's message by guessing its id
            // (spec §7.4 — per-key). The spool fast-path checks the .meta's api key; the log lookup filters by it.
            if (ctx.Items[ApiKeyMiddleware.ApiKeyItem] is not ApiKey callerKey)
                return Results.Unauthorized();

            var raw = id.StartsWith("spl_", StringComparison.OrdinalIgnoreCase) ? id[4..] : id;
            if (!Guid.TryParse(raw, out var guid))
                return Results.BadRequest(new { error = "Invalid message id." });

            var file = $"{guid}.eml";
            bool OwnedIn(string dir)
            {
                var path = Path.Combine(dir, file);
                return File.Exists(path) && SpoolMeta.Peek(path)?.ApiKeyId == callerKey.Id;
            }
            if (OwnedIn(spool.IncomingDir)) return Status(id, "queued");
            if (OwnedIn(spool.ProcessingDir)) return Status(id, "processing");
            if (OwnedIn(spool.FailedDir)) return Status(id, "failed");

            var row = await logs.GetBySpoolIdAsync(guid.ToString(), callerKey.Id, ct);
            if (row is null) return Results.NotFound(new { id, status = "unknown" });

            // Clamp to the documented status enum (spec §7.4: queued|processing|delivered|retrying|failed).
            var status = row.Event switch
            {
                "Delivered" => "delivered",
                "Retrying" => "retrying",
                "Failed" => "failed",
                _ => "queued",
            };
            return Results.Ok(new { id, status, provider = row.Provider, deliveredAt = row.LoggedAt, durationMs = row.DurationMs });
        });

        static IResult Status(string id, string status) => Results.Ok(new { id, status });
    }

    /// <summary>Dashboard read/stats API + admin, reachable only on the web port (spec §9.2).</summary>
    public static void MapDashboardApi(this IEndpointRouteBuilder app, int webPort)
    {
        // No auth, any port (§14.4). Liveness ("status") never blocks on SQL; all probes are best-effort,
        // non-throwing, and short-budget so monitors get a fast answer. Three states (spec §14.4):
        //   healthy  — everything nominal                                    -> 200
        //   degraded — SQL unreachable (mail still flows via the spool)      -> 200
        //   critical — intake suspended (disk critically low)                -> 503
        app.MapGet("/health", async (
            SpoolDirectory spool, IDatabaseHealth db, ILogMaintenance maintenance, MinuteCounterRing ring,
            IntakeState intake, IOptions<ListenerOptions> listener, CancellationToken ct) =>
        {
            var connected = await db.IsReachableAsync(ct);

            // dbSizeMb is best-effort: only attempt it when SQL is reachable, and swallow any failure
            // so a slow/unhealthy database can never make /health hang or throw.
            long? dbSizeMb = null;
            if (connected)
            {
                try { dbSizeMb = (await maintenance.GetDatabaseSizeBytesAsync(ct)) / (1024 * 1024); }
                catch { /* best-effort — leave null */ }
            }

            long? diskFreeMb = null;
            try { diskFreeMb = new DriveInfo(spool.Root).AvailableFreeSpace / (1024 * 1024); }
            catch { /* best-effort — leave null */ }

            var ports = listener.Value.EffectivePorts;
            var suspended = intake.Level == IntakeLevel.Suspended;
            var status = suspended ? "critical" : connected ? "healthy" : "degraded";
            var message = suspended
                ? "Disk space critically low — SMTP intake suspended"
                : connected ? null
                : "SQL Server unavailable — mail flow unaffected; UI log unavailable";

            var payload = new
            {
                status,
                message,
                version = Version,
                startedAtUtc = ProcessStartUtc,
                uptimeSeconds = (long)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds,
                timeUtc = DateTime.UtcNow,
                last5Minutes = new { received = ring.SumReceived(5), sentToProvider = ring.SumDelivered(5) },
                spool = new
                {
                    incoming = Directory.EnumerateFiles(spool.IncomingDir, "*.eml").Count(),
                    processing = Directory.EnumerateFiles(spool.ProcessingDir, "*.eml").Count(),
                    failed = Directory.EnumerateFiles(spool.FailedDir, "*.eml").Count(),
                    diskFreeMb,
                },
                sql = new { connected, dbSizeMb },
                smtp = new { listening = ports.Length > 0, ports },
            };
            return suspended
                ? Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(payload);
        });

        app.MapMetrics(webPort);   // unauthenticated Prometheus /metrics, dashboard port only (see MetricsEndpoints)

        var group = app.MapGroup("/api").RequireLocalPort(webPort);

        group.MapGet("/stats", async (ICounterReader counters, SpoolDirectory spool, IntakeState intake, CancellationToken ct) =>
        {
            var totals = await counters.GetTodayAsync(ct);
            return Results.Ok(new
            {
                totals.Received, totals.Delivered, totals.Failed, totals.Retried, totals.Denied,
                spool = SpoolCounts(spool),
                intake = intake.Level.ToString(),   // Normal | Throttled | Suspended (spec §14.1)
            });
        });

        group.MapGet("/stats/throughput", (MinuteCounterRing ring) => Results.Ok(ring.Snapshot()));

        group.MapGet("/stats/relays", async (IRelayRepository relays, ICounterReader counters, RelayConcurrencyTracker concurrency, CancellationToken ct) =>
        {
            var records = await relays.GetAllAsync(ct);
            var totals = (await counters.GetTodayByRelayAsync(ct)).ToDictionary(t => t.RelayId);
            var inFlight = concurrency.Snapshot();
            return Results.Ok(records.Select(r =>
            {
                totals.TryGetValue(r.Id, out var t);
                return new
                {
                    r.Id, r.Name, provider = r.Provider.ToString(), r.IsDefault, r.Enabled,
                    received = t?.Received ?? 0, delivered = t?.Delivered ?? 0, failed = t?.Failed ?? 0,
                    inFlight = inFlight.GetValueOrDefault(r.Id, 0),
                };
            }));
        });

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
                Events = q["event"].Count > 0
                    ? q["event"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : null,
                IngestSource = NullIfEmpty(q["source"].ToString()),
                ApiKeyId = int.TryParse(q["apiKeyId"], out var akid) ? akid : null,
                FromDomain = NullIfEmpty(q["fromDomain"].ToString()),
                ToDomain = NullIfEmpty(q["toDomain"].ToString()),
                RelayName = NullIfEmpty(q["relay"].ToString()),
                RoutingRuleName = NullIfEmpty(q["rule"].ToString()),
                Subject = NullIfEmpty(q["subject"].ToString()),
                Tag = NullIfEmpty(q["tag"].ToString()),
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

        group.MapGet("/messages/{id:long}", async (long id, IMessageLogQuery logs, CancellationToken ct) =>
        {
            var detail = await logs.GetByIdAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapAuth();            // /api/auth/* (see AuthEndpoints)
        group.MapSettings();        // /api/settings (see SettingsEndpoints)
        group.MapServiceOps();      // /api/service/* (see ServiceEndpoints)
        group.MapRelayRouting();    // /api/relays/* and /api/routing/* (see RoutingEndpoints)
        group.MapProviderTest();    // /api/config/test-provider (see ProviderTestEndpoints)
        group.MapLocalInbox();      // /api/local/messages (see LocalInboxEndpoints)
        group.MapFailedMessages();  // /api/failed/* (see FailedMessageEndpoints)
        group.MapSmtpCredentials(); // /api/smtp-credentials (see SmtpCredentialEndpoints)

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

        group.MapDelete("/keys/{id:int}", async (int id, IApiKeyRepository keys, ApiKeyCache cache, CancellationToken ct) =>
        {
            if (!await keys.RevokeAsync(id, ct)) return Results.NotFound();
            cache.Invalidate(id);   // stop the key working now, not after the 30s cache TTL (spec §17.4)
            return Results.Ok();
        });
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
