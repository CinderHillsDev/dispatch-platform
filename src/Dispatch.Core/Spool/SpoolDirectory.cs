using System.Threading.Channels;

namespace Dispatch.Core.Spool;

/// <summary>
/// Path helper + worker "doorbell" for the durable spool queue (spec §6, §19.3).
/// The three subdirectories are the source of truth for all in-flight messages.
/// </summary>
public sealed class SpoolDirectory
{
    public string Root { get; }
    public string IncomingDir { get; }
    public string ProcessingDir { get; }
    public string FailedDir { get; }

    /// <summary>Where the local/dev (None) provider captures messages instead of delivering externally.</summary>
    public string CapturedDir { get; }

    /// <summary>Where the Express size-pressure purge writes weekly JSONL exports of relay_log / audit_log
    /// rows before deleting them, so an emergency near-10GB purge never silently loses history.</summary>
    public string ArchiveDir { get; }

    // Bounded doorbell: filenames only. DropOldest because a dropped wake-up is harmless —
    // the FileSystemWatcher and startup sweep guarantee files are still discovered.
    private readonly Channel<string> _doorbell =
        Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public SpoolDirectory(string root)
    {
        Root = Path.GetFullPath(root);
        IncomingDir = Path.Combine(Root, "incoming");
        ProcessingDir = Path.Combine(Root, "processing");
        FailedDir = Path.Combine(Root, "failed");
        CapturedDir = Path.Combine(Root, "captured");
        ArchiveDir = Path.Combine(Root, "archive");
        // The spool holds full message bodies (and the .dispatch-key lives in the data dir) — restrict to the
        // owner (700) on non-Windows so other local users can't read spooled mail.
        CreatePrivate(Root);
        CreatePrivate(IncomingDir);
        CreatePrivate(ProcessingDir);
        CreatePrivate(FailedDir);
        CreatePrivate(CapturedDir);
        CreatePrivate(ArchiveDir);
    }

    private static void CreatePrivate(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } // 700
            catch { /* best-effort — a restrictive umask or unsupported FS shouldn't block startup */ }
        }
    }

    public string IncomingPath(Guid id) => Path.Combine(IncomingDir, $"{id}.eml");
    public string ProcessingPath(string filename) => Path.Combine(ProcessingDir, filename);
    public string FailedPath(string filename) => Path.Combine(FailedDir, filename);

    /// <summary>Wake an idle worker. Best-effort — never blocks.</summary>
    public void Signal(string filename) => _doorbell.Writer.TryWrite(filename);

    public ValueTask<string> WaitAsync(CancellationToken ct) => _doorbell.Reader.ReadAsync(ct);
}
