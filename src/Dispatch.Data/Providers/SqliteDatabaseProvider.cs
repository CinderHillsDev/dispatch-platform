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
        PerTableSizeReporting = false,  // needs the dbstat module, absent from most builds
    };

    public IReadOnlySet<string> DistinctiveKeywords { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "filename" };

    public IReadOnlySet<string> Aliases { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sqlite", "sqlite3", "bundled", "embedded" };

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, o => o.MigrationsAssembly(MigrationsAssembly));

    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public string UtcNowSql => "CURRENT_TIMESTAMP";

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
    /// Taken from the files rather than page counts, so it includes the -wal and -shm sidecars an operator
    /// sees when they look at the directory.
    /// </summary>
    public Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default)
    {
        var path = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:") return Task.FromResult(0L);

        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var info = new FileInfo(path + suffix);
            if (info.Exists) total += info.Length;
        }
        return Task.FromResult(total);
    }

    /// <summary>
    /// Per-table bytes need the dbstat virtual table (SQLITE_ENABLE_DBSTAT_VTAB), which is not compiled into
    /// every SQLitePCLRaw build. When unavailable this returns 0 rather than estimating: the storage view
    /// degrades to whole-database size plus exact row counts, and an invented per-table number that looked
    /// authoritative would be worse than an absent one.
    /// </summary>
    public async Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default)
    {
        var name = ProviderBootstrap.SafeIdentifier(table);
        try
        {
            return await ProviderBootstrap.ScalarAsync(db, $"""
                SELECT CAST(COALESCE(SUM(pgsize), 0) AS bigint) FROM dbstat
                WHERE name = '{name}'
                   OR name IN (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = '{name}');
                """, ct);
        }
        catch (SqliteException)
        {
            return 0;
        }
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
