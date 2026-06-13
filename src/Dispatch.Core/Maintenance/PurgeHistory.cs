using System.Collections.Concurrent;

namespace Dispatch.Core.Maintenance;

/// <summary>The outcome of one purge cycle (spec §9.2 purge history).</summary>
public sealed record PurgeRunResult(
    DateTime RanAtUtc,
    bool Manual,
    int SpoolFilesDeleted,
    int LogRowsDeleted,
    long DatabaseSizeBytes);

/// <summary>In-memory ring of the most recent purge runs (last 10), for the Settings → purge-history table.</summary>
public sealed class PurgeHistory
{
    private const int Capacity = 10;
    private readonly ConcurrentQueue<PurgeRunResult> _runs = new();

    public void Record(PurgeRunResult result)
    {
        _runs.Enqueue(result);
        while (_runs.Count > Capacity && _runs.TryDequeue(out _)) { }
    }

    /// <summary>Most recent runs first.</summary>
    public IReadOnlyList<PurgeRunResult> Snapshot() => _runs.Reverse().ToList();
}
