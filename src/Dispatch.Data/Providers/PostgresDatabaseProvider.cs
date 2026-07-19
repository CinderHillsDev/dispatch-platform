using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dispatch.Data.Providers;

/// <summary>PostgreSQL. A bring-your-own-server backend; the engine Dispatch shipped on before 0.7.</summary>
public sealed class PostgresDatabaseProvider : IDatabaseProvider
{
    public DatabaseProvider Id => DatabaseProvider.Postgres;
    public string DisplayName => "PostgreSQL";
    public string MigrationsAssembly => "Dispatch.Data.Postgres";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        FilteredIndexes = true,
        CoveringIndexes = true,
        CaseSensitiveLike = true,      // native behaviour; nothing to configure
        PlainIdentityInsert = true,
        PerTableSizeReporting = true,
    };

    // "Host" is Npgsql's spelling and no other supported client uses it as the primary server keyword.
    public IReadOnlySet<string> DistinctiveKeywords { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "host" };

    public IReadOnlySet<string> Aliases { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "postgres", "postgresql", "npgsql", "pgsql" };

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(MigrationsAssembly));

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public string UtcNowSql => "now()";

    public string? IndexFilter(IndexPredicate predicate) => predicate switch
    {
        IndexPredicate.DefaultRelay => "is_default",
        IndexPredicate.LiveApiKey => "NOT revoked",
        IndexPredicate.ApiKeyAttributedLog => "api_key_id IS NOT NULL",
        _ => null,
    };

    public string? CoveringIndexAnnotation => "Npgsql:IndexInclude";

    public Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>INSERT .. ON CONFLICT DO UPDATE. The qualified reference on the right-hand side is required:
    /// an unqualified column there is ambiguous between the existing row and the proposed one.</summary>
    public string CounterUpsertSql(string column) => $"""
        INSERT INTO relay_counters (date, relay_id, {column}) VALUES (@date, @relayId, 1)
        ON CONFLICT (date, relay_id) DO UPDATE SET {column} = relay_counters.{column} + 1;
        """;

    public async Task EnsureDatabaseAsync(string connectionString, ILogger? log, CancellationToken ct = default)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The connection string must specify Database.");

        // The target may not exist yet, so connect to the "postgres" maintenance database.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using var cn = new NpgsqlConnection(maintenance);
        await ProviderBootstrap.OpenWithRetryAsync(cn, log, ct);

        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", cn);
        check.Parameters.AddWithValue("n", database);
        if (await check.ExecuteScalarAsync(ct) is not null) return;

        // CREATE DATABASE cannot be parameterised or run inside a transaction. The name comes from our own
        // configuration, not user input; double-quoting allows mixed case and reserved words.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", cn);
        await create.ExecuteNonQueryAsync(ct);
        log?.LogInformation("Created database {Database}", database);
    }

    public Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default) =>
        ProviderBootstrap.ScalarAsync(db, "SELECT CAST(pg_database_size(current_database()) AS bigint);", ct);

    // to_regclass keeps the lookup safe and yields NULL (→ 0) when the table does not exist yet;
    // pg_total_relation_size covers data, indexes and TOAST.
    public Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default) =>
        ProviderBootstrap.ScalarAsync(db,
            $"SELECT CAST(COALESCE(pg_total_relation_size(to_regclass('{ProviderBootstrap.SafeIdentifier(table)}')), 0) AS bigint);", ct);

    public async Task ReclaimSpaceAsync(DbContext db, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        // VACUUM cannot run inside a transaction block. FULL rewrites the table and returns space to the OS,
        // so pg_database_size drops and the size-pressure trigger clears.
        foreach (var table in tables)
            await db.Database.ExecuteSqlRawAsync($"VACUUM (FULL) {ProviderBootstrap.SafeIdentifier(table)};", ct);
    }
}
