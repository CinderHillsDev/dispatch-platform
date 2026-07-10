using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core.Maintenance;

/// <summary>Hands a batch of database rows to a sink (archive them) - invoked by the size-pressure purge
/// just before the rows are deleted, so an emergency near-10GB purge never silently loses history.</summary>
public delegate Task ArchiveRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct);

/// <summary>
/// Appends database rows to weekly JSON Lines files (one JSON object per line) under an archive directory,
/// grouped by the ISO week of each row's timestamp column - e.g. <c>relay_log-2026-W26.jsonl</c>. JSONL is
/// append-friendly and trivially re-ingestible/greppable. Only the (single-threaded) purge worker writes
/// here, so plain append is safe.
/// </summary>
public sealed class JsonlRowArchive(string archiveDir)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Appends <paramref name="rows"/> to per-ISO-week files named
    /// <c>{table}-{isoYear}-W{week}.jsonl</c>, choosing the week from each row's <paramref name="timestampColumn"/>.</summary>
    public void Append(string table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string timestampColumn)
    {
        if (rows.Count == 0) return;
        Directory.CreateDirectory(archiveDir);
        foreach (var week in rows.GroupBy(r => WeekKey(r, timestampColumn)))
        {
            var path = Path.Combine(archiveDir, $"{table}-{week.Key}.jsonl");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var row in week)
                writer.WriteLine(JsonSerializer.Serialize(row, Json));
        }
    }

    private static string WeekKey(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var v) && v is DateTime dt)
            return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(dt):D4}-W{ISOWeek.GetWeekOfYear(dt):D2}");
        return "undated";
    }
}
