using Dapper;
using Npgsql;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Ensures the target database exists and applies ordered, embedded SQL migrations once each,
/// tracking applied versions in <c>schema_version</c> (spec §6.11, §12). Idempotent and safe to
/// run on every startup.
/// </summary>
public sealed class DatabaseInitializer(SqlConnectionFactory factory, ILogger<DatabaseInitializer> log)
{
    private const string MigrationPrefix = ".Migrations.";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ConnectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Connection string must specify a Database.");

        await EnsureDatabaseAsync(builder, database, ct);
        await EnsureSchemaVersionTableAsync(ct);
        await ApplyMigrationsAsync(database, ct);
    }

    private async Task EnsureDatabaseAsync(NpgsqlConnectionStringBuilder builder, string database, CancellationToken ct)
    {
        // Connect to the "postgres" maintenance database to check for / create the target database.
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };
        await using var cn = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        await OpenWithRetryAsync(cn, ct);

        var exists = await cn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM pg_database WHERE datname = @database", new { database });
        if (exists is null)
        {
            // CREATE DATABASE cannot be parameterised or run inside a transaction; the database name comes
            // from our own config, not user input. Double-quote to allow mixed case / reserved words.
            await cn.ExecuteAsync($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"");
            log.LogInformation("Created database {Database}", database);
        }
    }

    private async Task EnsureSchemaVersionTableAsync(CancellationToken ct)
    {
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version     int          NOT NULL PRIMARY KEY,
                script_name varchar(256) NOT NULL,
                applied_at  timestamptz  NOT NULL DEFAULT now()
            );
            """);
    }

    private async Task ApplyMigrationsAsync(string database, CancellationToken ct)
    {
        await using var cn = await factory.OpenAsync(ct);
        var applied = (await cn.QueryAsync<int>("SELECT version FROM schema_version")).ToHashSet();

        foreach (var (version, name, sql) in LoadMigrations())
        {
            if (applied.Contains(version))
                continue;

            await using var tx = await cn.BeginTransactionAsync(ct);
            try
            {
                await cn.ExecuteAsync(sql, transaction: tx);
                await cn.ExecuteAsync(
                    "INSERT INTO schema_version (version, script_name) VALUES (@version, @name)",
                    new { version, name }, tx);
                await tx.CommitAsync(ct);
                log.LogInformation("Applied migration {Version} ({Name})", version, name);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    /// <summary>Opens a connection, retrying for up to ~60s so a just-started Postgres container (CI/docker) is tolerated.</summary>
    private async Task OpenWithRetryAsync(NpgsqlConnection cn, CancellationToken ct)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cn.OpenAsync(ct);
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                if (attempt == 1) log.LogInformation("Waiting for PostgreSQL to accept connections…");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var asm = typeof(DatabaseInitializer).Assembly;
        var migrations = new List<(int, string, string)>();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            var idx = resource.IndexOf(MigrationPrefix, StringComparison.Ordinal);
            if (idx < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = resource[(idx + MigrationPrefix.Length)..];          // e.g. "0001_init.sql"
            var versionText = name.Split('_', 2)[0];
            if (!int.TryParse(versionText, out var version))
                continue;

            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add((version, name, reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Item1);
    }
}
