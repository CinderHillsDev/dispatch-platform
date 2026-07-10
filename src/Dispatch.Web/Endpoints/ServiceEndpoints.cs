using Dispatch.Core.Spool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Dispatch.Web.Endpoints;

/// <summary>Operational endpoints used by upgrades/monitoring (spec §9.3, §14, §16).</summary>
public static class ServiceEndpoints
{
    private static readonly DateTime StartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void MapServiceOps(this RouteGroupBuilder group)
    {
        // System / About (spec §9.2): version, uptime, OS/runtime, and the log file location for the UI.
        group.MapGet("/system", () => Results.Ok(new
        {
            version = typeof(ServiceEndpoints).Assembly.GetName().Version?.ToString() ?? "dev",
            uptimeSeconds = (long)(DateTime.UtcNow - StartedUtc).TotalSeconds,
            startedAtUtc = StartedUtc,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            logDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("DISPATCH_LOG_DIR") ?? "logs"),
        }));

        // Waits for the in-flight spool (incoming + processing) to clear, for a graceful pre-upgrade stop.
        group.MapPost("/service/drain", async (SpoolDirectory spool, HttpContext ctx) =>
        {
            var remaining = await DrainAsync(spool, ctx.RequestAborted);
            return Results.Ok(new { drained = remaining == 0, remaining });
        });

        // Graceful restart (spec §9.3): drain the queue, then stop the host so the service manager
        // (systemd / Windows SC, both configured to restart on exit) brings it back up.
        group.MapPost("/service/restart", async (SpoolDirectory spool, IHostApplicationLifetime life, HttpContext ctx) =>
        {
            var remaining = await DrainAsync(spool, ctx.RequestAborted);
            // Stop shortly after responding so the client receives the acknowledgement first.
            _ = Task.Run(async () => { await Task.Delay(500); life.StopApplication(); });
            return Results.Ok(new { restarting = true, drained = remaining == 0, remaining });
        });

        // Download the current rolling Serilog file (spec §9.3 GET /api/logs/download).
        group.MapGet("/logs/download", () =>
        {
            var dir = Environment.GetEnvironmentVariable("DISPATCH_LOG_DIR") ?? "logs";
            if (!Directory.Exists(dir))
                return Results.NotFound(new { error = "No log directory configured." });

            var newest = new DirectoryInfo(dir).GetFiles("dispatch-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null)
                return Results.NotFound(new { error = "No log files found." });

            // FileShare.ReadWrite: Serilog keeps the current file open for writing.
            var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Results.File(stream, "text/plain", newest.Name);
        });
    }

    private static async Task<int> DrainAsync(SpoolDirectory spool, CancellationToken ct)
    {
        int Remaining() =>
            Directory.EnumerateFiles(spool.IncomingDir, "*.eml").Count() +
            Directory.EnumerateFiles(spool.ProcessingDir, "*.eml").Count();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Remaining() > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
        return Remaining();
    }
}
