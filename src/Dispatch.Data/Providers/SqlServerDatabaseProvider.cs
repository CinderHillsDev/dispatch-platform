using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Providers;

/// <summary>
/// Microsoft SQL Server - a bring-your-own-server backend for sites that already run and staff one.
///
/// Dispatch does not install SQL Server and does not target SQL Express: Express's 10 GB database cap is
/// what drove the original size-pressure purge subsystem, and re-adopting that constraint for a bundled
/// database is exactly what the SQLite default exists to avoid.
///
/// Tables land in the connection's default schema, which is dbo unless the server says otherwise.
/// </summary>
public sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    public DatabaseProvider Id => DatabaseProvider.SqlServer;
    public string DisplayName => "Microsoft SQL Server";
    public string MigrationsAssembly => "Dispatch.Data.SqlServer";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        FilteredIndexes = true,
        CoveringIndexes = true,
        CaseSensitiveLike = true,       // enforced by the collation set at database creation
        PlainIdentityInsert = false,    // needs SET IDENTITY_INSERT; see DatabaseMigrator
        PerTableSizeReporting = true,
    };

    public IReadOnlySet<string> DistinctiveKeywords { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "initial catalog", "trusted_connection", "integrated security",
            "trustservercertificate", "applicationintent", "multisubnetfailover",
        };

    public IReadOnlySet<string> Aliases { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sqlserver", "mssql", "sql server", "sqlsrv" };

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, o => o.MigrationsAssembly(MigrationsAssembly));

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    /// <summary>CURRENT_TIMESTAMP is LOCAL server time here; Dispatch stores UTC.</summary>
    public string UtcNowSql => "SYSUTCDATETIME()";

    public string? IndexFilter(IndexPredicate predicate) => predicate switch
    {
        // SQL Server filtered indexes require an explicit comparison - a bare bit column is not a predicate.
        IndexPredicate.DefaultRelay => "[is_default] = 1",
        IndexPredicate.LiveApiKey => "[revoked] = 0",
        IndexPredicate.ApiKeyAttributedLog => "[api_key_id] IS NOT NULL",
        _ => null,
    };

    public string? CoveringIndexAnnotation => "SqlServer:Include";

    public Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// SQL Server has no ON CONFLICT. MERGE is the documented upsert, and HOLDLOCK on the target is what
    /// makes it atomic against a concurrent MERGE for the same key - without it two workers can both miss
    /// the row and both insert, violating the unique constraint. This is the contended hot path, so that
    /// hint is load-bearing rather than defensive.
    /// </summary>
    public string CounterUpsertSql(string column) => $"""
        MERGE relay_counters WITH (HOLDLOCK) AS target
        USING (SELECT @date AS [date], @relayId AS relay_id) AS source
            ON target.[date] = source.[date] AND target.relay_id = source.relay_id
        WHEN MATCHED THEN UPDATE SET {column} = target.{column} + 1
        WHEN NOT MATCHED THEN INSERT ([date], relay_id, {column}) VALUES (source.[date], source.relay_id, 1);
        """;

    public async Task EnsureDatabaseAsync(string connectionString, ILogger? log, CancellationToken ct = default)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The connection string must specify Initial Catalog (Database).");

        var maintenance = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var cn = new SqlConnection(maintenance);
        await ProviderBootstrap.OpenWithRetryAsync(cn, log, ct);

        await using var check = new SqlCommand("SELECT 1 FROM sys.databases WHERE name = @n", cn);
        check.Parameters.AddWithValue("@n", database);
        if (await check.ExecuteScalarAsync(ct) is not null) return;

        // A case-sensitive collation, so LIKE matches PostgreSQL and SQLite. SQL Server instances are very
        // often installed with a case-INSENSITIVE default collation, which would silently widen every
        // Message Log subject search, tag match and audit search relative to the other engines. _BIN2 also
        // makes identifier and value comparisons deterministic rather than culture-dependent.
        await using var create = new SqlCommand(
            $"CREATE DATABASE [{database.Replace("]", "]]")}] COLLATE Latin1_General_BIN2", cn);
        await create.ExecuteNonQueryAsync(ct);
        log?.LogInformation("Created database {Database} with a case-sensitive collation", database);
    }

    public Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default) =>
        // sys.database_files is per-database and counts pages of 8 KB, data and log alike.
        ProviderBootstrap.ScalarAsync(db,
            "SELECT CAST(COALESCE(SUM(CAST(size AS bigint)) * 8192, 0) AS bigint) FROM sys.database_files;", ct);

    public Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default) =>
        // Data plus indexes, mirroring pg_total_relation_size. Pages are 8 KB.
        ProviderBootstrap.ScalarAsync(db, $"""
            SELECT CAST(COALESCE(SUM(a.total_pages), 0) * 8192 AS bigint)
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id
            JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE t.name = '{ProviderBootstrap.SafeIdentifier(table)}';
            """, ct);

    /// <summary>
    /// SQL Server reclaims space by rebuilding indexes and then shrinking the file. Shrinking is disruptive
    /// and fragments indexes, which is why it runs only as an explicit maintenance action.
    /// </summary>
    public async Task ReclaimSpaceAsync(DbContext db, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        foreach (var table in tables)
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER INDEX ALL ON [{ProviderBootstrap.SafeIdentifier(table)}] REBUILD;", ct);

        await db.Database.ExecuteSqlRawAsync("DBCC SHRINKDATABASE(0) WITH NO_INFOMSGS;", ct);
    }
}
