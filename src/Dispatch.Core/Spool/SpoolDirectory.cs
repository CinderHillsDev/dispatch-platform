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
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(ProcessingDir);
        Directory.CreateDirectory(FailedDir);
    }

    public string IncomingPath(Guid id) => Path.Combine(IncomingDir, $"{id}.eml");
    public string ProcessingPath(string filename) => Path.Combine(ProcessingDir, filename);
    public string FailedPath(string filename) => Path.Combine(FailedDir, filename);

    /// <summary>Wake an idle worker. Best-effort — never blocks.</summary>
    public void Signal(string filename) => _doorbell.Writer.TryWrite(filename);

    public ValueTask<string> WaitAsync(CancellationToken ct) => _doorbell.Reader.ReadAsync(ct);
}
