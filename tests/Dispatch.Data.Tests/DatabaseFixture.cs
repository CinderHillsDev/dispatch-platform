using Dapper;
using Dispatch.Data;
using Dispatch.Data.Dialects;
using Microsoft.Data.Sqlite;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// Provides an isolated, migrated database for the repository integration tests, on whichever engine is
/// selected. The same 27 tests run unchanged against both backends — that equivalence is the whole point,
/// so a query that silently behaves differently on one engine fails the suite rather than reaching users.
///
/// Engine selection:
///   * <c>DISPATCH_TEST_SQL</c> set  → PostgreSQL. Creates a uniquely-named database on that server and
///                                     drops it on dispose. This is the CI path and is unchanged.
///   * unset                         → SQLite, in a temp file deleted on dispose.
///
/// The SQLite default means a developer with no database running still executes the full suite instead of
/// silently skipping it. CI should run the suite twice — once with <c>DISPATCH_TEST_SQL</c> pointed at
/// Postgres, once without — to cover both engines.
///
/// <c>DISPATCH_REQUIRE_SQL</c> keeps its meaning: it asserts the PostgreSQL path is genuinely configured,
/// so a misconfigured pipeline fails loudly instead of quietly falling back to SQLite and reporting green.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }
    public bool Available => ConnectionString is not null;
    public SqlConnectionFactory Factory => new(ConnectionString!);
    public ISqlDialect Dialect => Factory.Dialect;
    public string Engine => Factory.Dialect.Name;

    private string? _baseConnection;
    private string? _dbName;
    private string? _sqlitePath;

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL");

        if (string.IsNullOrWhiteSpace(baseConn))
        {
            if (IsTruthy(Environment.GetEnvironmentVariable("DISPATCH_REQUIRE_SQL")))
                throw new InvalidOperationException(
                    "DISPATCH_REQUIRE_SQL is set but DISPATCH_TEST_SQL is not - the PostgreSQL integration tests cannot run.");

            _sqlitePath = Path.Combine(Path.GetTempPath(), $"dispatchtest_{Guid.NewGuid():N}.db");
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = _sqlitePath }.ConnectionString;
        }
        else
        {
            _baseConnection = baseConn;
            // Postgres folds unquoted identifiers to lowercase; Guid's "N" format is already lowercase hex.
            _dbName = "dispatchtest_" + Guid.NewGuid().ToString("N");
            ConnectionString = new NpgsqlConnectionStringBuilder(baseConn) { Database = _dbName }.ConnectionString;
            await WaitForServerAsync(TimeSpan.FromSeconds(90));
        }

        var initializer = new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    private async Task WaitForServerAsync(TimeSpan timeout)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(_baseConnection!) { Database = "postgres", Timeout = 5 }.ConnectionString;
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var cn = new NpgsqlConnection(maintenance);
                await cn.OpenAsync();
                await cn.ExecuteAsync("SELECT 1");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(2000);
            }
        }
        throw new InvalidOperationException(
            $"PostgreSQL server at the DISPATCH_TEST_SQL endpoint was not reachable within {timeout.TotalSeconds:F0}s.", last);
    }

    private static bool IsTruthy(string? v) =>
        !string.IsNullOrEmpty(v) && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    public async Task DisposeAsync()
    {
        if (_sqlitePath is not null)
        {
            // Pooled connections keep the file handle open; clear them so the delete can't race on Windows.
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(_sqlitePath + suffix);
            return;
        }

        if (_baseConnection is null || _dbName is null) return;

        var maintenance = new NpgsqlConnectionStringBuilder(_baseConnection) { Database = "postgres" }.ConnectionString;
        await using var pg = new NpgsqlConnection(maintenance);
        await pg.OpenAsync();
        // FORCE (PG 13+) terminates any lingering backends so the drop can't hang on a stray connection.
        await pg.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);");
    }
}
