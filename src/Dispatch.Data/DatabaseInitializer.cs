using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Brings the database up to the current schema on every startup. Idempotent, and safe against a brand-new
/// database, an already-current one, or one created before 0.7 moved the schema to EF Core.
///
/// Three cases, in the order they are checked:
///
///  1. <b>Already on EF migrations</b> - apply whatever is pending. The normal path.
///
///  2. <b>Pre-0.7 database</b> - a `schema_version` table and no `__EFMigrationsHistory`. These were built
///     by the hand-written PostgreSQL scripts and hold live mail history, so re-creating their tables would
///     fail on "relation already exists" and, if it somehow succeeded, would be catastrophic. The baseline
///     migration is recorded as already-applied instead, and later migrations then run normally.
///
///     This is only sound because the EF model was verified to produce the same schema those scripts do:
///     83/83 columns and defaults identical, 27/28 indexes, the difference being an index EF adds on a
///     foreign key by convention.
///
///  3. <b>Empty database</b> - apply everything from scratch.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<DispatchDbContext> contexts,
    DatabaseBootstrap bootstrap,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Create the database/file and wait for the server to accept connections. EF's MigrateAsync can
        // create a database, but it will not wait for one that is still starting - which is exactly what a
        // compose stack or a freshly-provisioned VM does on first boot.
        await bootstrap.EnsureDatabaseAsync(ct);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var provider = db.Database.ProviderName;

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        if (applied.Count == 0 && await HasPreEfSchemaAsync(db, ct))
        {
            await AdoptPreEfSchemaAsync(db, ct);
            log.LogInformation(
                "Adopted an existing pre-0.7 schema: recorded the baseline migration as applied without " +
                "re-creating tables that already hold data. [{Provider}]", provider);
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
    /// True when this database was built by the pre-0.7 migration runner: a schema_version table AND a real
    /// table alongside it, so an orphaned schema_version from a failed run is not mistaken for a populated
    /// schema and used to skip creating everything.
    /// </summary>
    public static async Task<bool> HasPreEfSchemaAsync(DispatchDbContext db, CancellationToken ct = default)
    {
        var tables = await ExistingTablesAsync(db, ct);
        return tables.Contains("schema_version") && tables.Contains("relay_log");
    }

    private static async Task AdoptPreEfSchemaAsync(DispatchDbContext db, CancellationToken ct)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException("No migrations found in the provider's migrations assembly.");

        // EF's own history repository, so the table is created with the shape and quoting this provider
        // expects rather than hand-written DDL that would differ per engine.
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())), ct);
    }

    private static async Task<HashSet<string>> ExistingTablesAsync(DispatchDbContext db, CancellationToken ct)
    {
        // Every engine names its catalog differently, and each needs a way to say "the tables of THIS
        // database" - without that restriction MySQL lists every table on the server, so an unrelated
        // database could make this claim a pre-0.7 schema exists.
        var sql = db.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" =>
                "SELECT name FROM sqlite_master WHERE type = 'table'",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()",
            // sys.tables, not INFORMATION_SCHEMA. Dispatch creates SQL Server databases with a
            // case-sensitive collation (Latin1_General_BIN2, so LIKE matches the other engines), which makes
            // identifier resolution case-sensitive too - and the view is really named
            // INFORMATION_SCHEMA.TABLES, so the lowercase spelling that works everywhere else fails here.
            // sys.tables is canonically lowercase and immune to that.
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                "SELECT name FROM sys.tables",
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
