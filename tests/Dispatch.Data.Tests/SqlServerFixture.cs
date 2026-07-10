using Dapper;
using Dispatch.Data;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// Spins up an isolated, uniquely-named database on the PostgreSQL server pointed to by the
/// <c>DISPATCH_TEST_SQL</c> environment variable, applies migrations, and drops it on dispose.
/// When the env var is unset the fixture is <see cref="Available"/> = false and tests early-return,
/// so the suite stays green without a database - UNLESS <c>DISPATCH_REQUIRE_SQL</c> is set (CI), in which
/// case a missing connection string is a hard failure so the integration tests can never silently skip.
/// Before applying migrations it waits for the server to accept connections (a fresh container can take
/// tens of seconds to become ready in CI), so a slow-starting container surfaces as a wait, not a flaky failure.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }
    public bool Available => ConnectionString is not null;
    public SqlConnectionFactory Factory => new(ConnectionString!);

    private string? _baseConnection;
    private string? _dbName;

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL");
        if (string.IsNullOrWhiteSpace(baseConn))
        {
            // CI sets DISPATCH_REQUIRE_SQL so a misconfigured pipeline fails loudly instead of skipping
            // every integration test and reporting a false green.
            if (IsTruthy(Environment.GetEnvironmentVariable("DISPATCH_REQUIRE_SQL")))
                throw new InvalidOperationException(
                    "DISPATCH_REQUIRE_SQL is set but DISPATCH_TEST_SQL is not - the PostgreSQL integration tests cannot run.");
            return;
        }

        _baseConnection = baseConn;
        // Postgres folds unquoted identifiers to lowercase; Guid's "N" format is already lowercase hex.
        _dbName = "dispatchtest_" + Guid.NewGuid().ToString("N");
        ConnectionString = new NpgsqlConnectionStringBuilder(baseConn) { Database = _dbName }.ConnectionString;

        await WaitForServerAsync(TimeSpan.FromSeconds(90));

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
        if (_baseConnection is null || _dbName is null) return;

        var maintenance = new NpgsqlConnectionStringBuilder(_baseConnection) { Database = "postgres" }.ConnectionString;
        await using var cn = new NpgsqlConnection(maintenance);
        await cn.OpenAsync();
        // FORCE (PG 13+) terminates any lingering backends so the drop can't hang on a stray connection.
        await cn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);");
    }
}
