using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Brings the database up to the current schema on every startup. Idempotent, and safe to run against a
/// brand-new database, an already-current one, or one created by the pre-EF hand-written migrations.
///
/// Three cases, in the order they are checked:
///
///  1. <b>Already on EF migrations</b> — __EFMigrationsHistory exists. Apply whatever is pending. The
///     normal path.
///
///  2. <b>Pre-EF database</b> — schema_version exists but __EFMigrationsHistory does not. These are live
///     PostgreSQL deployments created by Migrations/Postgres/0001-0007. Their tables already hold data, so
///     running InitialSchema would fail on "relation already exists" and, if it somehow succeeded, would be
///     catastrophic. Instead the baseline is recorded as already-applied and later migrations proceed
///     normally. This is only sound because the EF model was verified to produce the same schema those
///     scripts do (83/83 columns, 27/28 indexes, the difference being an FK index EF adds by convention).
///
///  3. <b>Empty database</b> — neither table. Apply everything from scratch.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<DispatchDbContext> contexts,
    DatabaseBootstrap bootstrap,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Create the database/file and wait for the server to accept connections. EF's MigrateAsync can
        // create a database, but not a PostgreSQL one it lacks permission to create, and it will not wait
        // for a container that is still starting.
        await bootstrap.EnsureDatabaseAsync(ct);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var provider = db.Database.ProviderName;

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        if (applied.Count == 0 && await HasLegacySchemaAsync(db, ct))
        {
            await AdoptLegacySchemaAsync(db, ct);
            log.LogInformation(
                "Adopted an existing pre-EF schema: recorded the baseline migration as applied without " +
                "re-creating tables. [{Provider}]", provider);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            log.LogInformation("Database schema is current. [{Provider}]", provider);
            return;
        }

        log.LogInformation("Applying {Count} migration(s): {Migrations} [{Provider}]",
            pending.Count, string.Join(", ", pending), provider);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// True when this database was created by the pre-EF migration runner — schema_version present, and at
    /// least one real table alongside it (so an orphaned schema_version from a failed run is not mistaken
    /// for a populated schema and used to skip creating everything).
    /// </summary>
    private static async Task<bool> HasLegacySchemaAsync(DispatchDbContext db, CancellationToken ct)
    {
        var tables = await ExistingTablesAsync(db, ct);
        return tables.Contains("schema_version") && tables.Contains("relay_log");
    }

    private async Task AdoptLegacySchemaAsync(DispatchDbContext db, CancellationToken ct)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException("No migrations found in the provider's migrations assembly.");

        // EF's own history repository, so the table is created with the shape and quoting this provider
        // expects rather than hand-written DDL that would differ per engine.
        var history = db.GetService<IHistoryRepository>();
        var createScript = history.GetCreateIfNotExistsScript();
        await db.Database.ExecuteSqlRawAsync(createScript, ct);

        var insert = history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion()));
        await db.Database.ExecuteSqlRawAsync(insert, ct);
    }

    private static async Task<HashSet<string>> ExistingTablesAsync(DispatchDbContext db, CancellationToken ct)
    {
        // information_schema covers Postgres, MySQL/MariaDB and SQL Server; SQLite has sqlite_master
        // instead. Each needs a different way to say "the current database's own tables" — without that
        // restriction, MySQL would list every table on the server and SQL Server would include system
        // schemas, so an unrelated database could make this claim a legacy schema exists.
        var sql = db.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" =>
                "SELECT name FROM sqlite_master WHERE type = 'table'",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()",
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                "SELECT table_name FROM information_schema.tables WHERE table_catalog = DB_NAME()",
            var other => throw new InvalidOperationException($"Unsupported EF provider '{other}'."),
        };

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0));
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
        return names;
    }
}
