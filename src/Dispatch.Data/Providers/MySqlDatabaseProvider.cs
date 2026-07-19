using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Dispatch.Data.Providers;

/// <summary>
/// MariaDB and MySQL — a bring-your-own-server backend, via Pomelo.
///
/// Pomelo rather than Oracle's MySql.EntityFrameworkCore for two independent reasons: Oracle's connector has
/// dropped MariaDB compatibility (the auth plugins diverged from MariaDB 10.4), and it ships under GPL-2.0
/// with a FOSS exception, which sits badly under an Apache-2.0 project that promises commercial
/// redistribution. Pomelo is MIT and supports both servers, detecting which at connect time.
///
/// This is the one supported engine with NO filtered indexes, so the "at most one default relay" invariant
/// cannot be a partial unique index here and is upheld by SqlRelayRepository instead — see
/// <see cref="ProviderCapabilities.FilteredIndexes"/>.
/// </summary>
public sealed class MySqlDatabaseProvider : IDatabaseProvider
{
    public DatabaseProvider Id => DatabaseProvider.MySql;
    public string DisplayName => "MariaDB / MySQL";
    public string MigrationsAssembly => "Dispatch.Data.MySql";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        FilteredIndexes = false,        // no partial indexes at all
        CoveringIndexes = false,        // no INCLUDE
        CaseSensitiveLike = true,       // enforced by the utf8mb4_bin collation set at database creation
        PlainIdentityInsert = true,
        PerTableSizeReporting = true,
    };

    public IReadOnlySet<string> DistinctiveKeywords { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "uid", "sslmode", "allowuservariables", "allowpublickeyretrieval",
            "treattinyasboolean", "server version", "guidformat",
        };

    public IReadOnlySet<string> Aliases { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mysql", "mariadb", "pomelo" };

    /// <summary>
    /// Fallback when the server cannot be reached during configuration. MariaDB rather than MySQL because
    /// MariaDB is the more conservative target: Pomelo avoids the MySQL-only syntax it would otherwise
    /// emit, so the generated SQL runs on both.
    /// </summary>
    private static readonly ServerVersion FallbackVersion = new MariaDbServerVersion(new Version(11, 4));

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseMySql(connectionString, DetectServerVersion(connectionString),
            o => o.MigrationsAssembly(MigrationsAssembly));

    /// <summary>
    /// MariaDB and MySQL have diverged enough that Pomelo generates different SQL for each, so the flavour
    /// is read from the server banner when possible.
    ///
    /// AutoDetect does that by OPENING A CONNECTION, which means configuring this provider would otherwise
    /// require a reachable server — breaking design-time scaffolding, offline tooling, and any startup that
    /// happens before the database is up. So a failure to detect falls back to a version rather than
    /// throwing. This is safe because it only affects query translation: migrations are generated at design
    /// time, where DesignTimeFactory pins the version explicitly.
    /// </summary>
    private static ServerVersion DetectServerVersion(string connectionString)
    {
        try
        {
            return ServerVersion.AutoDetect(connectionString);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException)
        {
            return FallbackVersion;
        }
    }

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <summary>CURRENT_TIMESTAMP is LOCAL session time here; Dispatch stores UTC.</summary>
    public string UtcNowSql => "UTC_TIMESTAMP()";

    /// <summary>No filtered indexes on any MySQL or MariaDB version. Callers handle null.</summary>
    public string? IndexFilter(IndexPredicate predicate) => null;

    public string? CoveringIndexAnnotation => null;

    public Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// INSERT .. ON DUPLICATE KEY UPDATE, which is atomic against the unique key on (date, relay_id).
    /// VALUES() is deprecated in MySQL 8.0.20+ but is the only form MariaDB also accepts, and the increment
    /// here references the existing row rather than the proposed one, so it is not needed.
    /// </summary>
    public string CounterUpsertSql(string column) => $"""
        INSERT INTO relay_counters (`date`, relay_id, {column}) VALUES (@date, @relayId, 1)
        ON DUPLICATE KEY UPDATE {column} = {column} + 1;
        """;

    public async Task EnsureDatabaseAsync(string connectionString, ILogger? log, CancellationToken ct = default)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The connection string must specify Database.");

        var maintenance = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;
        await using var cn = new MySqlConnection(maintenance);
        await ProviderBootstrap.OpenWithRetryAsync(cn, log, ct);

        // utf8mb4 so the full Unicode range survives — subjects and addresses carry emoji and non-BMP
        // characters, and MySQL's legacy "utf8" is 3-byte and would truncate them.
        //
        // utf8mb4_bin is case-SENSITIVE. Both servers default to a case-insensitive collation, which would
        // silently widen every LIKE in the Message Log, tag matching and audit search relative to the other
        // engines. Matching the established behaviour is the safe default; changing search semantics should
        // be a deliberate product decision, not a consequence of which database an operator runs.
        await using var create = new MySqlCommand(
            $"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "``")}` " +
            "CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", cn);
        await create.ExecuteNonQueryAsync(ct);
    }

    public Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default) =>
        // information_schema sizes are an estimate for InnoDB, refreshed from the storage engine's
        // statistics rather than measured. Good enough for a storage view; not to the byte.
        ProviderBootstrap.ScalarAsync(db, """
            SELECT CAST(COALESCE(SUM(data_length + index_length), 0) AS SIGNED)
            FROM information_schema.tables WHERE table_schema = DATABASE();
            """, ct);

    public Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default) =>
        ProviderBootstrap.ScalarAsync(db, $"""
            SELECT CAST(COALESCE(SUM(data_length + index_length), 0) AS SIGNED)
            FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = '{ProviderBootstrap.SafeIdentifier(table)}';
            """, ct);

    /// <summary>
    /// InnoDB reclaims space with a null ALTER TABLE, which rebuilds the table in place. On a large
    /// relay_log this is slow and holds a metadata lock, hence maintenance-only.
    /// </summary>
    public async Task ReclaimSpaceAsync(DbContext db, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        foreach (var table in tables)
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE `{ProviderBootstrap.SafeIdentifier(table)}` ENGINE=InnoDB;", ct);
    }
}
