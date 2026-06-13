using Dispatch.Core.Spool;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.Core.Maintenance;

/// <summary>Probes free disk space (bytes) on the spool partition. Injectable so tests can simulate pressure.</summary>
public delegate long FreeSpaceProbe(string spoolRoot);

/// <summary>
/// Fast disk back-pressure monitor (spec §14.1). On a short ~10s timer it reads free space on the spool
/// partition and updates <see cref="IntakeState"/> so inbound SMTP throttles/suspends well before the disk
/// fills. The slower <see cref="PurgeWorker"/> evaluates the same probe on its cycle as a backstop. The
/// free-space source is injected (defaults to <see cref="DriveInfo"/>) so it can be tested without real
/// disk pressure. All work is best-effort — a probe failure is logged and never crashes the service.
/// </summary>
public sealed class DiskMonitor : BackgroundService
{
    /// <summary>Default probe: available free bytes on the volume hosting <paramref name="spoolRoot"/>.</summary>
    public static long DriveFreeSpace(string spoolRoot)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(spoolRoot));
        if (string.IsNullOrEmpty(root)) return long.MaxValue;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly SpoolDirectory _spool;
    private readonly IntakeState _intake;
    private readonly FreeSpaceProbe _probe;
    private readonly ILogger<DiskMonitor> _log;

    public DiskMonitor(SpoolDirectory spool, IntakeState intake, ILogger<DiskMonitor> log)
        : this(spool, intake, DriveFreeSpace, log) { }

    /// <summary>Test/internal constructor allowing a simulated free-space probe.</summary>
    public DiskMonitor(SpoolDirectory spool, IntakeState intake, FreeSpaceProbe probe, ILogger<DiskMonitor> log)
    {
        _spool = spool;
        _intake = intake;
        _probe = probe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Evaluate();   // act immediately on startup
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
            Evaluate();
        }
    }

    /// <summary>
    /// Reads free space and applies the corresponding intake level, logging transitions. Shared by the
    /// fast timer and <see cref="PurgeWorker"/>. Internal so tests can drive it directly.
    /// </summary>
    internal void Evaluate()
    {
        long freeBytes;
        try { freeBytes = _probe(_spool.Root); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Disk free-space probe failed (intake level unchanged)");
            return;
        }

        var previous = _intake.Level;
        var next = _intake.Apply(freeBytes);

        if (next != previous)
        {
            const double mb = 1024d * 1024d;
            switch (next)
            {
                case IntakeLevel.Suspended:
                    _log.LogError("Spool disk critically low ({FreeMb:F0} MB free) — SMTP intake SUSPENDED", freeBytes / mb);
                    break;
                case IntakeLevel.Throttled:
                    _log.LogWarning("Spool disk low ({FreeMb:F0} MB free) — SMTP intake THROTTLED", freeBytes / mb);
                    break;
                case IntakeLevel.Normal:
                    _log.LogInformation("Spool disk recovered ({FreeMb:F0} MB free) — SMTP intake NORMAL", freeBytes / mb);
                    break;
            }
        }
        else if (next == IntakeLevel.Normal && freeBytes < IntakeState.WarnBytes)
        {
            // Below the warn threshold but not yet throttling — surface it without spamming on transitions.
            _log.LogWarning("Spool disk free space low: {FreeMb:F0} MB", freeBytes / (1024d * 1024d));
        }
    }
}
