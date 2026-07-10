using System.Globalization;
using System.Text;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Relays;
using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>Unauthenticated Prometheus metrics endpoint (text exposition format 0.0.4).</summary>
public static class MetricsEndpoints
{
    public static void MapMetrics(this IEndpointRouteBuilder app, int webPort)
    {
        app.MapGet("/metrics", async (
            ICounterReader counters, IRelayRepository relays, RelayConcurrencyTracker concurrency,
            MinuteCounterRing ring, SpoolDirectory spool, IDatabaseHealth db, CancellationToken ct) =>
        {
            var totals = await counters.GetTodayAsync(ct);
            var perRelay = await counters.GetTodayByRelayAsync(ct);
            var names = (await relays.GetAllAsync(ct)).ToDictionary(r => r.Id, r => r.Name);
            var inFlight = concurrency.Snapshot();
            var reachable = await db.IsReachableAsync(ct);

            var sb = new StringBuilder();
            Gauge(sb, "dispatch_up", "1 if the service is running", [(null, 1)]);
            Gauge(sb, "dispatch_database_reachable", "1 if the database is reachable", [(null, reachable ? 1 : 0)]);

            Gauge(sb, "dispatch_messages_today", "Messages today by event",
            [
                ("event=\"received\"", totals.Received),
                ("event=\"delivered\"", totals.Delivered),
                ("event=\"failed\"", totals.Failed),
                ("event=\"retried\"", totals.Retried),
                ("event=\"denied\"", totals.Denied),
            ]);

            Gauge(sb, "dispatch_messages_last5m", "Messages in the last 5 minutes",
            [
                ("event=\"received\"", ring.SumReceived(5)),
                ("event=\"sent\"", ring.SumDelivered(5)),
            ]);

            Gauge(sb, "dispatch_spool_files", "Spool files by state",
            [
                ("state=\"incoming\"", Count(spool.IncomingDir)),
                ("state=\"processing\"", Count(spool.ProcessingDir)),
                ("state=\"failed\"", Count(spool.FailedDir)),
            ]);

            Gauge(sb, "dispatch_relay_inflight", "Dispatches in flight per relay",
                names.Select(kv => ((string?)$"relay=\"{Escape(kv.Value)}\"", (double)inFlight.GetValueOrDefault(kv.Key, 0))));

            Gauge(sb, "dispatch_relay_delivered_today", "Delivered today per relay",
                perRelay.Select(r => ((string?)$"relay=\"{Escape(names.GetValueOrDefault(r.RelayId, r.RelayId.ToString()))}\"", (double)r.Delivered)));

            Gauge(sb, "dispatch_relay_failed_today", "Failed today per relay",
                perRelay.Select(r => ((string?)$"relay=\"{Escape(names.GetValueOrDefault(r.RelayId, r.RelayId.ToString()))}\"", (double)r.Failed)));

            return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
        })
        // Unauthenticated, but only on the dashboard port - never exposed on the ingestion listener.
        .AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Connection.LocalPort == webPort ? await next(ctx) : Results.NotFound());
    }

    private static void Gauge(StringBuilder sb, string name, string help, IEnumerable<(string? Labels, double Value)> series)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        foreach (var (labels, value) in series)
        {
            sb.Append(name);
            if (labels is not null) sb.Append('{').Append(labels).Append('}');
            sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static int Count(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.eml").Count() : 0;

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
