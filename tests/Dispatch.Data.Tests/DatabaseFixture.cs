using Dispatch.Data;
using Dispatch.Data.Providers;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;

namespace Dispatch.Data.Tests;

/// <summary>
/// Provides an isolated, migrated database on whichever engine is selected, so the same repository tests
/// run unchanged against all four backends. That equivalence is the point: a query that behaves differently
/// on one engine fails the suite rather than reaching users.
///
/// Engine selection, via <c>DISPATCH_TEST_ENGINE</c> (sqlite | postgres | sqlserver | mysql):
///   * unset, or "sqlite"  → SQLite in a temp file. The default, so a developer with no server running
///                           still executes the full suite instead of silently skipping it.
///   * anything else       → that engine, using <c>DISPATCH_TEST_SQL</c> as the base connection string.
///                           A uniquely-named database is created per run and dropped on dispose.
///
/// For backwards compatibility, setting <c>DISPATCH_TEST_SQL</c> without <c>DISPATCH_TEST_ENGINE</c> means
/// PostgreSQL - that is what the variable meant before other engines existed, and CI still sets it that way.
///
/// <c>DISPATCH_REQUIRE_SQL</c> keeps its meaning: it asserts a server engine is genuinely configured, so a
/// misconfigured pipeline fails loudly instead of quietly falling back to SQLite and reporting green.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }
    public bool Available => ConnectionString is not null;
    public DatabaseProvider Provider { get; private set; } = DatabaseProvider.Sqlite;
    public string Engine => Provider.ToString();


    public IDbContextFactory<DispatchDbContext> Contexts =>
        DispatchDbContextFactory.Create(Provider, ConnectionString!);

    /// <summary>The provider implementation, for repositories that need engine-specific SQL.</summary>
    public IDatabaseProvider DbProvider => DatabaseProviders.Get(Provider);

    private string? _baseConnection;
    private string? _dbName;
    private string? _sqlitePath;

    public async Task InitializeAsync()
    {
        var engine = Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE");
        var baseConn = Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL");

        if (string.IsNullOrWhiteSpace(engine))
            engine = string.IsNullOrWhiteSpace(baseConn) ? "sqlite" : "postgres";

        if (string.Equals(engine, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTruthy(Environment.GetEnvironmentVariable("DISPATCH_REQUIRE_SQL")))
                throw new InvalidOperationException(
                    "DISPATCH_REQUIRE_SQL is set but no server engine is configured - the integration tests cannot run.");

            Provider = DatabaseProvider.Sqlite;
            _sqlitePath = Path.Combine(Path.GetTempPath(), $"dispatchtest_{Guid.NewGuid():N}.db");
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = _sqlitePath }.ConnectionString;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(baseConn))
                throw new InvalidOperationException(
                    $"DISPATCH_TEST_ENGINE is '{engine}' but DISPATCH_TEST_SQL is not set.");

            Provider = DatabaseProviderResolver.Resolve(baseConn, engine);
            _baseConnection = baseConn;
            // Lowercase hex: Postgres folds unquoted identifiers to lowercase, and MySQL database names are
            // case-sensitive on Linux filesystems.
            _dbName = "dispatchtest_" + Guid.NewGuid().ToString("N");
            ConnectionString = WithDatabase(baseConn, _dbName);
        }

        var bootstrap = new DatabaseBootstrap(Provider, ConnectionString, NullLogger<DatabaseBootstrap>.Instance);
        await new DatabaseInitializer(Contexts, bootstrap, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync();
    }

    private string WithDatabase(string baseConnection, string database) => Provider switch
    {
        DatabaseProvider.Postgres => new NpgsqlConnectionStringBuilder(baseConnection) { Database = database }.ConnectionString,
        DatabaseProvider.SqlServer => new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = database }.ConnectionString,
        DatabaseProvider.MySql => new MySqlConnectionStringBuilder(baseConnection) { Database = database }.ConnectionString,
        _ => throw new InvalidOperationException($"{Provider} has no server-side database to name."),
    };

    /// <summary>Runs an administrative statement. Plain ADO: these are DDL on a maintenance connection,
    /// outside the model, and the test project does not otherwise need a micro-ORM.</summary>
    private static async Task ExecuteAsync(System.Data.Common.DbConnection cn, string sql)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static bool IsTruthy(string? v) =>
        !string.IsNullOrEmpty(v) && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    public async Task DisposeAsync()
    {
        if (_sqlitePath is not null)
        {
            // Pooled connections keep the file handle open; clear them so the delete cannot race.
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(_sqlitePath + suffix);
            return;
        }

        if (_baseConnection is null || _dbName is null) return;

        switch (Provider)
        {
            case DatabaseProvider.Postgres:
            {
                var maintenance = new NpgsqlConnectionStringBuilder(_baseConnection) { Database = "postgres" }.ConnectionString;
                await using var cn = new NpgsqlConnection(maintenance);
                await cn.OpenAsync();
                // FORCE (PG 13+) terminates lingering backends so the drop cannot hang on a stray connection.
                await ExecuteAsync(cn, $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);");
                break;
            }
            case DatabaseProvider.SqlServer:
            {
                var maintenance = new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = "master" }.ConnectionString;
                SqlConnection.ClearAllPools();
                await using var cn = new SqlConnection(maintenance);
                await cn.OpenAsync();
                // SINGLE_USER WITH ROLLBACK IMMEDIATE is SQL Server's equivalent of FORCE.
                await ExecuteAsync(cn,
                    $"IF DB_ID('{_dbName}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{_dbName}]; END");
                break;
            }
            case DatabaseProvider.MySql:
            {
                var maintenance = new MySqlConnectionStringBuilder(_baseConnection) { Database = "" }.ConnectionString;
                await using var cn = new MySqlConnection(maintenance);
                await cn.OpenAsync();
                await ExecuteAsync(cn, $"DROP DATABASE IF EXISTS `{_dbName}`;");
                break;
            }
        }
    }
}
