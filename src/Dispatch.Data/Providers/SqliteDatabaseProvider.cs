using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Providers;

/// <summary>
/// SQLite - the bundled backend and the default deployment. A file beside the service, no database server
/// to install, which removes the largest single piece of install and ops burden from the appliance and the
/// Windows installer.
///
/// This is safe because Dispatch never uses the database as a coordination substrate: the work queue is the
/// filesystem spool (SpoolWorkerPool claims .eml files via FileSystemWatcher and a per-relay SemaphoreSlim),
/// and SQL is only touched after a provider responds. Every writer is therefore a thread inside one process,
/// which is what SQLite's single-writer model handles well - WAL lets readers run uncontended while the one
/// writer commits.
///
/// Timestamps are ISO-8601 TEXT. Microsoft.Data.Sqlite writes DateTime as "yyyy-MM-dd HH:mm:ss.FFFFFFF" and
/// SQLite's own CURRENT_TIMESTAMP emits "yyyy-MM-dd HH:mm:ss" - a shared prefix, so lexicographic ordering
/// matches chronological ordering and the Message Log's keyset cursor stays correct. Both UTC. Do not
/// introduce a third format.
/// </summary>
public sealed class SqliteDatabaseProvider : IDatabaseProvider
{
    public DatabaseProvider Id => DatabaseProvider.Sqlite;
    public string DisplayName => "SQLite";
    public string MigrationsAssembly => "Dispatch.Data.Sqlite";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        FilteredIndexes = true,
        CoveringIndexes = false,        // no INCLUDE; key columns only
        CaseSensitiveLike = true,       // via PRAGMA case_sensitive_like, set per connection below
        PlainIdentityInsert = true,
        PerTableSizeReporting = true,   // computed; see GetTableSizeBytesAsync
    };

    public IReadOnlySet<string> DistinctiveKeywords { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "filename" };

    public IReadOnlySet<string> Aliases { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sqlite", "sqlite3", "bundled", "embedded" };

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, o => o.MigrationsAssembly(MigrationsAssembly));

    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public string UtcNowSql => "CURRENT_TIMESTAMP";

    public string? DefaultCollation => null;

    public string? IndexFilter(IndexPredicate predicate) => predicate switch
    {
        IndexPredicate.DefaultRelay => "is_default",
        IndexPredicate.LiveApiKey => "NOT revoked",
        IndexPredicate.ApiKeyAttributedLog => "api_key_id IS NOT NULL",
        _ => null,
    };

    public string? CoveringIndexAnnotation => null;

    public async Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken ct = default)
    {
        // None of these persist in the file, so all must be set on every connection.
        //   busy_timeout        - makes a concurrent writer wait for the write lock instead of failing
        //                         instantly with SQLITE_BUSY. Without it, ingest under load surfaces
        //                         "database is locked".
        //   foreign_keys        - SQLite disables FK enforcement by default; the schema declares FKs and
        //                         the repositories rely on them.
        //   case_sensitive_like - SQLite's LIKE is ASCII-case-insensitive by default and PostgreSQL's is
        //                         not. Every LIKE in the codebase (Message Log subject, tag matching
        //                         against a JSON array, audit search) was written against case-sensitive
        //                         semantics, so without this the choice of backend would silently widen
        //                         user-facing search results.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON; PRAGMA case_sensitive_like = ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>SQLite's UPSERT. Unlike PostgreSQL it accepts either a bare or a table-qualified column on
    /// the right-hand side; the qualified form is used so both engines read identically.</summary>
    public string CounterUpsertSql(string column) => $"""
        INSERT INTO relay_counters (date, relay_id, {column}) VALUES (@date, @relayId, 1)
        ON CONFLICT (date, relay_id) DO UPDATE SET {column} = relay_counters.{column} + 1;
        """;

    /// <summary>
    /// There is no server and no database to create - the file appears on first connect. This ensures the
    /// containing directory exists and sets the two pragmas that DO persist in the file header.
    /// </summary>
    public async Task EnsureDatabaseAsync(string connectionString, ILogger? log, CancellationToken ct = default)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("The connection string must specify Data Source.");

        if (path != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                log?.LogInformation("Created database directory {Directory}", dir);
            }
        }

        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        await OnConnectionOpenedAsync(cn, ct);

        // WAL is what makes the single-writer model workable: readers never block the writer and the writer
        // never blocks readers. synchronous=NORMAL is the standard WAL pairing - it drops the per-commit
        // fsync, which is the main throughput limit, while staying crash-safe; the residual risk is losing
        // the last few commits on host power loss, not corruption.
        await using var pragma = cn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The database's logical size: page count times page size.
    ///
    /// NOT the sum of the files on disk, which was the obvious first choice and is wrong. In WAL mode the
    /// -wal sidecar holds committed-but-not-yet-checkpointed pages and grows with write volume - 3,000
    /// inserts produced a 180 MB WAL against roughly 3 MB of actual data. Reporting that told the operator
    /// their message log was sixty times its real size, and because the WAL keeps growing between calls it
    /// also let a single table appear larger than the whole database.
    ///
    /// page_count already accounts for pages living in the WAL, so this is stable, checkpoint-independent,
    /// and is what the file settles at once SQLite checkpoints.
    /// </summary>
    public Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default) =>
        ProviderBootstrap.ScalarAsync(db,
            "SELECT (SELECT * FROM pragma_page_count()) * (SELECT * FROM pragma_page_size());", ct);

    /// <summary>
    /// On-disk bytes attributable to one table, including its share of index and page overhead.
    ///
    /// SQLite has no per-table size function. The dbstat virtual table would give an exact page-level
    /// answer, but it requires SQLITE_ENABLE_DBSTAT_VTAB and is absent from the SQLitePCLRaw builds this
    /// ships with (verified against both 2.1.x and 3.x). So this measures instead of guessing:
    ///
    ///   1. the database's real size on disk, from the file;
    ///   2. each table's real content size, from octet lengths of its actual rows;
    ///   3. the on-disk total split across tables in proportion to (2).
    ///
    /// Every input is measured. What is inferred is only the *split* of shared overhead - indexes, page
    /// slack, freelist - which SQLite does not attribute per table. A table with disproportionately many
    /// indexes (relay_log has nine) therefore reads slightly low. That is a bounded inaccuracy in a storage
    /// breakdown, and far more useful than reporting nothing: the whole point of the page is telling an
    /// operator which table is consuming their disk.
    /// </summary>
    public async Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default)
    {
        var name = ProviderBootstrap.SafeIdentifier(table);

        var totalBytes = await GetDatabaseSizeBytesAsync(db, ct);
        if (totalBytes == 0) return 0;

        var payloads = await PayloadByTableAsync(db, ct);
        if (!payloads.TryGetValue(name, out var mine) || mine == 0) return 0;

        var allPayload = payloads.Values.Sum();
        if (allPayload == 0) return 0;

        return (long)(totalBytes * ((double)mine / allPayload));
    }

    /// <summary>
    /// Estimated content bytes per user table: mean row width from a sample, times the exact row count.
    ///
    /// Sampled rather than summed over every row because this backs an admin page that must stay responsive
    /// on a relay_log with millions of rows - a full scan of every column would take seconds. The sample
    /// deliberately takes rows from BOTH ends of the table: relay_log rows grow and shrink over time
    /// (subjects, error text), so reading only the oldest 1,000 would misjudge a table whose recent traffic
    /// looks different from its history. Both ends are index seeks, so this stays cheap.
    /// </summary>
    private static async Task<Dictionary<string, long>> PayloadByTableAsync(DbContext db, CancellationToken ct)
    {
        const int samplePerEnd = 500;
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            var tables = new List<string>();
            await using (var cmd = connection.CreateCommand())
            {
                // Real tables only: sqlite_* are internal, and EF's history table is not user data.
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' " +
                    "AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '@_@_EF%' ESCAPE '@';";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
            }

            foreach (var name in tables)
            {
                // CAST(col AS BLOB) gives the byte length on every SQLite version, where octet_length()
                // needs 3.43+ and length() would count characters rather than bytes.
                string columnSum;
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT group_concat('COALESCE(length(CAST(\"'||name||'\" AS BLOB)),0)', '+') "
                        + $"FROM pragma_table_info('{name}');";
                    columnSum = await cmd.ExecuteScalarAsync(ct) as string ?? "";
                }
                if (string.IsNullOrEmpty(columnSum)) continue;

                await using var measure = connection.CreateCommand();
                measure.CommandText = $"""
                    SELECT (SELECT COUNT(*) FROM "{name}"),
                           (SELECT AVG(p) FROM (
                                -- Each end wrapped in its own subquery: SQLite rejects ORDER BY inside a
                                -- UNION ALL branch.
                                SELECT p FROM (SELECT {columnSum} AS p FROM "{name}" ORDER BY rowid ASC  LIMIT {samplePerEnd})
                                UNION ALL
                                SELECT p FROM (SELECT {columnSum} AS p FROM "{name}" ORDER BY rowid DESC LIMIT {samplePerEnd})));
                    """;
                await using var r = await measure.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) continue;

                var rows = r.IsDBNull(0) ? 0L : r.GetInt64(0);
                var avg = r.IsDBNull(1) ? 0d : r.GetDouble(1);
                result[name] = (long)(rows * avg);
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        return result;
    }

    /// <summary>
    /// SQLite's VACUUM is whole-database and cannot target one table, so the list is ignored. It rewrites
    /// the entire file, needs roughly the database's size again in free disk, and holds an exclusive lock
    /// throughout.
    /// </summary>
    public async Task ReclaimSpaceAsync(DbContext db, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        // Checkpoint first so WAL content folds into the main file and its space is actually reclaimed.
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
        await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
    }
}
