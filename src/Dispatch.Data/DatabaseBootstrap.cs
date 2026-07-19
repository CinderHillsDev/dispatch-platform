using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace Dispatch.Data;

/// <summary>
/// Everything that has to happen before EF can connect: create the database if it does not exist, and wait
/// for a server that is still starting.
///
/// EF's MigrateAsync can create a database, but it cannot wait for one — a Postgres container in
/// docker-compose or a SQL Server service still coming up will simply refuse the connection and the service
/// will crash-loop. The retry here is what makes "docker compose up" work on a cold start.
///
/// For the bundled SQLite deployment this reduces to ensuring the directory exists; the file itself is
/// created on first connect.
/// </summary>
public sealed class DatabaseBootstrap(
    DatabaseProvider provider, string connectionString, ILogger<DatabaseBootstrap>? log = null)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 30;   // ~60s

    public async Task EnsureDatabaseAsync(CancellationToken ct = default)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite: EnsureSqliteDirectory(); break;
            case DatabaseProvider.Postgres: await EnsurePostgresAsync(ct); break;
            case DatabaseProvider.SqlServer: await EnsureSqlServerAsync(ct); break;
            case DatabaseProvider.MySql: await EnsureMySqlAsync(ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private void EnsureSqliteDirectory()
    {
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:") return;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            log?.LogInformation("Created database directory {Directory}", dir);
        }
    }

    private async Task EnsurePostgresAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = Required(builder.Database, "Database");

        // The target database may not exist yet, so connect to the "postgres" maintenance database.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using var cn = new NpgsqlConnection(maintenance);
        await OpenWithRetryAsync(cn, ct);

        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", cn);
        check.Parameters.AddWithValue("n", database);
        if (await check.ExecuteScalarAsync(ct) is not null) return;

        // CREATE DATABASE cannot be parameterised or run in a transaction. The name comes from our own
        // configuration, not user input; double-quoting allows mixed case and reserved words.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", cn);
        await create.ExecuteNonQueryAsync(ct);
        log?.LogInformation("Created database {Database}", database);
    }

    private async Task EnsureSqlServerAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = Required(builder.InitialCatalog, "Initial Catalog / Database");

        var maintenance = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var cn = new SqlConnection(maintenance);
        await OpenWithRetryAsync(cn, ct);

        await using var check = new SqlCommand("SELECT 1 FROM sys.databases WHERE name = @n", cn);
        check.Parameters.AddWithValue("@n", database);
        if (await check.ExecuteScalarAsync(ct) is not null) return;

        await using var create = new SqlCommand($"CREATE DATABASE [{database.Replace("]", "]]")}]", cn);
        await create.ExecuteNonQueryAsync(ct);
        log?.LogInformation("Created database {Database}", database);
    }

    private async Task EnsureMySqlAsync(CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = Required(builder.Database, "Database");

        var maintenance = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;
        await using var cn = new MySqlConnection(maintenance);
        await OpenWithRetryAsync(cn, ct);

        // utf8mb4 so the full Unicode range survives — subjects and addresses carry emoji and non-BMP
        // characters, and MySQL's legacy "utf8" is 3-byte and would truncate them.
        //
        // The collation is deliberately case-SENSITIVE (_bin). MySQL and MariaDB default to a
        // case-insensitive collation, which would silently widen every LIKE in the Message Log, tag
        // matching and audit search relative to the other three engines. Matching the existing behaviour
        // is the safe default; changing search semantics should be a deliberate product decision, not a
        // side effect of which database an operator happens to run.
        await using var create = new MySqlCommand(
            $"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "``")}` " +
            "CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", cn);
        await create.ExecuteNonQueryAsync(ct);
    }

    private static string Required(string? value, string keyword) =>
        !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidOperationException($"The connection string must specify {keyword}.");

    /// <summary>
    /// Opens a connection, retrying for ~60s so a database container that is still starting is tolerated
    /// rather than crash-looping the service.
    /// </summary>
    private async Task OpenWithRetryAsync(System.Data.Common.DbConnection cn, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cn.OpenAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is not OperationCanceledException)
            {
                if (attempt == 1) log?.LogInformation("Waiting for the database server to accept connections…");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }
}
