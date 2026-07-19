using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatch.Data.Dialects;

/// <summary>PostgreSQL dialect - the original backend, unchanged in behaviour (spec §6.10, §12).</summary>
public sealed class PostgresDialect(ILogger? log = null) : ISqlDialect
{
    public string Name => "Postgres";

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public Task OnConnectionOpenedAsync(DbConnection cn, CancellationToken ct = default) => Task.CompletedTask;

    // Npgsql maps DateTime with Kind=Unspecified and a zero time-of-day to a Postgres `date` cleanly.
    public object DateParam(DateTime date) => date.Date;

    public string OlderThanDays(string column, string daysParam) =>
        $"{column} < CURRENT_TIMESTAMP - {daysParam} * interval '1 day'";

    public string FormatDate(string column) => $"to_char({column}, 'YYYY-MM-DD')";

    public async Task<long> GetDatabaseSizeBytesAsync(DbConnection cn, CancellationToken ct = default) =>
        await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT CAST(pg_database_size(current_database()) AS bigint);", cancellationToken: ct));

    // Passing the name through to_regclass keeps the lookup safe and returns NULL (→ 0) if the table
    // doesn't exist yet. pg_total_relation_size covers data + indexes + TOAST.
    public async Task<long> GetTableSizeBytesAsync(DbConnection cn, string table, CancellationToken ct = default) =>
        await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT CAST(COALESCE(pg_total_relation_size(to_regclass(@table)), 0) AS bigint);",
            new { table }, cancellationToken: ct));

    public async Task ReclaimSpaceAsync(DbConnection cn, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        // VACUUM cannot run inside a transaction block; each statement runs on its own. FULL rewrites the
        // table and returns space to the OS so pg_database_size drops and the size-pressure trigger clears.
        // Table names come from our own call sites, never user input.
        foreach (var table in tables)
            await cn.ExecuteAsync(new CommandDefinition($"VACUUM (FULL) {table};", cancellationToken: ct));
    }

    public async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Connection string must specify a Database.");

        // Connect to the "postgres" maintenance database to check for / create the target database.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using var cn = new NpgsqlConnection(maintenance);
        await OpenWithRetryAsync(cn, ct);

        var exists = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM pg_database WHERE datname = @database", new { database }, cancellationToken: ct));
        if (exists is null)
        {
            // CREATE DATABASE cannot be parameterised or run inside a transaction; the database name comes
            // from our own config, not user input. Double-quote to allow mixed case / reserved words.
            await cn.ExecuteAsync($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"");
            log?.LogInformation("Created database {Database}", database);
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
                if (attempt == 1) log?.LogInformation("Waiting for PostgreSQL to accept connections…");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
