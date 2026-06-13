using Dapper;
using Dispatch.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// Spins up an isolated, uniquely-named database on the SQL server pointed to by the
/// <c>DISPATCH_TEST_SQL</c> environment variable, applies migrations, and drops it on dispose.
/// When the env var is unset the fixture is <see cref="Available"/> = false and tests early-return,
/// so the suite stays green without a database.
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
        if (string.IsNullOrWhiteSpace(baseConn)) return;

        _baseConnection = baseConn;
        _dbName = "DispatchTest_" + Guid.NewGuid().ToString("N");
        ConnectionString = new SqlConnectionStringBuilder(baseConn) { InitialCatalog = _dbName }.ConnectionString;

        var initializer = new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_baseConnection is null || _dbName is null) return;

        var master = new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = "master" }.ConnectionString;
        await using var cn = new SqlConnection(master);
        await cn.OpenAsync();
        await cn.ExecuteAsync(
            $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];");
    }
}
