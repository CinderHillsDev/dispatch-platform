using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Dialects;

/// <summary>
/// SQLite dialect - the embedded backend for single-node deployments (the OVF appliance, the Windows
/// installer, and single-VM cloud deploys), where running a separate PostgreSQL server is pure install
/// and ops burden.
///
/// This is safe here because Dispatch never uses the database as a coordination substrate: the work queue
/// is the filesystem spool (SpoolWorkerPool claims .eml files via FileSystemWatcher + per-relay
/// SemaphoreSlim), and SQL is only touched after a provider responds. Every writer is therefore a thread
/// inside one process, which is exactly the shape SQLite's single-writer model handles well - WAL lets
/// readers run uncontended while the one writer commits.
///
/// Timestamps are stored as ISO-8601 TEXT. Microsoft.Data.Sqlite writes DateTime as
/// "yyyy-MM-dd HH:mm:ss.FFFFFFF" and SQLite's own CURRENT_TIMESTAMP / datetime() emit
/// "yyyy-MM-dd HH:mm:ss" - a shared prefix, so lexicographic ordering matches chronological ordering and
/// the keyset cursor (logged_at, id) stays correct. Both are UTC. Do not introduce a third format.
/// </summary>
public sealed class SqliteDialect : ISqlDialect
{
    private readonly ILogger? log;

    public SqliteDialect(ILogger? log = null)
    {
        this.log = log;
        SqliteTypeHandlers.EnsureRegistered();
    }

    public string Name => "Sqlite";

    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public async Task OnConnectionOpenedAsync(DbConnection cn, CancellationToken ct = default)
    {
        // None of these persist in the file, so all must be set on every connection.
        //   busy_timeout       - makes a concurrent writer wait for the write lock instead of failing
        //                        instantly with SQLITE_BUSY. Without it, ingest under load surfaces
        //                        "database is locked".
        //   foreign_keys       - SQLite disables FK enforcement by default; the schema declares FKs and the
        //                        repositories rely on them, so enforcement must be turned on explicitly.
        //   case_sensitive_like - SQLite's LIKE is ASCII-case-insensitive by default; Postgres's is not.
        //                        Every LIKE in the codebase (Message Log subject, tag matching against a
        //                        JSON array, audit search, config key prefixes) was written against
        //                        case-sensitive semantics, so without this a backend swap would silently
        //                        widen user-facing search results. Changing search behaviour may well be
        //                        worth doing - but as a deliberate product change, not a side effect.
        await cn.ExecuteAsync(new CommandDefinition(
            "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON; PRAGMA case_sensitive_like = ON;",
            cancellationToken: ct));
    }

    // SQLite has no date type: a bare "yyyy-MM-dd" is what CURRENT_DATE writes and what the column holds.
    // Binding a DateTime here would serialise as "yyyy-MM-dd 00:00:00", which sorts AFTER every bare-date
    // row for the same day and would silently drop the boundary day from range queries.
    public object DateParam(DateTime date) => date.ToString("yyyy-MM-dd");

    // '-' || 7 || ' days' → '-7 days'. datetime('now') is UTC, matching how timestamps are stored.
    public string OlderThanDays(string column, string daysParam) =>
        $"{column} < datetime('now', '-' || {daysParam} || ' days')";

    public string FormatDate(string column) => $"strftime('%Y-%m-%d', {column})";

    /// <summary>
    /// On-disk bytes for the database, taken from the actual files rather than page counts, so it includes
    /// the -wal and -shm sidecars an operator sees when they look at the directory.
    /// </summary>
    public Task<long> GetDatabaseSizeBytesAsync(DbConnection cn, CancellationToken ct = default)
    {
        var path = new SqliteConnectionStringBuilder(cn.ConnectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
            return Task.FromResult(0L);

        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var info = new FileInfo(path + suffix);
            if (info.Exists) total += info.Length;
        }
        return Task.FromResult(total);
    }

    /// <summary>
    /// Per-table bytes require the dbstat virtual table (SQLITE_ENABLE_DBSTAT_VTAB), which is not compiled
    /// into every SQLitePCLRaw build. When it is unavailable we return 0 rather than estimating: the storage
    /// view degrades to whole-database size plus row counts, which are exact. A fabricated per-table number
    /// that looked authoritative would be worse than an absent one.
    /// </summary>
    public async Task<long> GetTableSizeBytesAsync(DbConnection cn, string table, CancellationToken ct = default)
    {
        try
        {
            // Table name comes from our own call sites, never user input. Counts the table's own pages plus
            // those of every index on it, mirroring pg_total_relation_size.
            return await cn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT CAST(COALESCE(SUM(pgsize), 0) AS bigint) FROM dbstat
                WHERE name = @table
                   OR name IN (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @table);
                """,
                new { table }, cancellationToken: ct));
        }
        catch (SqliteException)
        {
            log?.LogDebug("dbstat is not available in this SQLite build; per-table size reported as 0.");
            return 0;
        }
    }

    /// <summary>
    /// SQLite's VACUUM is whole-database and cannot target one table, so the table list is ignored and a
    /// single VACUUM runs. It rewrites the entire file, needs roughly the database's size again in free
    /// disk, and takes an exclusive lock for the duration - callers must treat it as a maintenance action,
    /// not something to run on the hot path.
    /// </summary>
    public async Task ReclaimSpaceAsync(DbConnection cn, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        // Checkpoint first so WAL content is folded into the main file and its space is actually reclaimed.
        await cn.ExecuteAsync(new CommandDefinition("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("VACUUM;", cancellationToken: ct));
    }

    /// <summary>
    /// There is no server to create a database on - the file is created on first open. All this does is
    /// ensure the containing directory exists and enable WAL, which unlike the per-connection pragmas is
    /// persisted in the file header and so only needs setting once.
    /// </summary>
    public async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct = default)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Connection string must specify a Data Source.");

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
        // fsync (the main throughput limit) while still being crash-safe; the residual risk is losing the
        // last few commits on host power loss, not corruption. Both persist in the file.
        await cn.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("PRAGMA synchronous = NORMAL;", cancellationToken: ct));
    }
}
