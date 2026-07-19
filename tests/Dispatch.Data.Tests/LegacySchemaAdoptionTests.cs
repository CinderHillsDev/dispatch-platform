using Dapper;
using Dispatch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dispatch.Data.Tests;

/// <summary>
/// The upgrade path for deployments that predate EF migrations.
///
/// These databases were created by the hand-written runner: seven .sql scripts tracked in a schema_version
/// table, no __EFMigrationsHistory. They hold live mail history. If DatabaseInitializer treated one as empty
/// it would try to CREATE TABLE over populated tables — failing loudly at best, and at worst only after
/// partially applying. So the initializer detects them and records the baseline migration as already
/// applied instead.
///
/// This test builds a genuine pre-EF database — the same .sql scripts, still embedded in Dispatch.Data for
/// exactly this purpose — puts a row in it, runs the initializer, and asserts the row survived and the
/// schema is now considered current.
///
/// PostgreSQL only, because that is the only engine with deployments in the field to upgrade. SQLite and
/// the two new engines have no pre-EF installations by definition.
/// </summary>
public class LegacySchemaAdoptionTests
{
    private static string? BaseConnection =>
        Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL") is { Length: > 0 } s
        && !string.Equals(Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE"), "sqlite", StringComparison.OrdinalIgnoreCase)
            ? s : null;

    [Fact]
    public async Task Adopts_a_pre_EF_database_without_touching_its_data()
    {
        if (BaseConnection is null) return;
        if (DatabaseProviderResolver.Resolve(BaseConnection, Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE"))
            != DatabaseProvider.Postgres) return;

        var dbName = "dispatchlegacy_" + Guid.NewGuid().ToString("N");
        var maintenance = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = "postgres" }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = dbName }.ConnectionString;

        await using (var admin = new NpgsqlConnection(maintenance))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            // ---- Build a database exactly as the pre-EF runner would have left it -------------------
            await using (var cn = new NpgsqlConnection(target))
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync("""
                    CREATE TABLE schema_version (
                        version     int          NOT NULL PRIMARY KEY,
                        script_name varchar(256) NOT NULL,
                        applied_at  timestamptz  NOT NULL DEFAULT now()
                    );
                    """);

                foreach (var (version, name, sql) in LegacyMigrations())
                {
                    await cn.ExecuteAsync(sql);
                    await cn.ExecuteAsync(
                        "INSERT INTO schema_version (version, script_name) VALUES (@version, @name)",
                        new { version, name });
                }

                // A row of "existing mail history" whose survival is the point of the whole exercise.
                await cn.ExecuteAsync("""
                    INSERT INTO relay_log (spool_id, event, status, from_address, from_domain,
                                           to_addresses, to_domain, subject)
                    VALUES ('legacy-row', 'Delivered', 'OK', 'a@x.com', 'x.com', '[]', 'y.com', 'pre-existing');
                    """);
            }

            // ---- Run the initializer over it --------------------------------------------------------
            var contexts = DispatchDbContextFactory.Create(DatabaseProvider.Postgres, target);
            await new DatabaseInitializer(
                    contexts,
                    new DatabaseBootstrap(DatabaseProvider.Postgres, target, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            // ---- The data is still there, and the schema is now considered current ------------------
            await using (var cn = new NpgsqlConnection(target))
            {
                await cn.OpenAsync();
                var survived = await cn.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM relay_log WHERE spool_id = 'legacy-row'");
                Assert.Equal(1, survived);
            }

            await using (var db = await contexts.CreateDbContextAsync())
            {
                Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            // Running again must be a no-op rather than an error: the service initialises on every start.
            await new DatabaseInitializer(
                    contexts,
                    new DatabaseBootstrap(DatabaseProvider.Postgres, target, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();
        }
        finally
        {
            await using var admin = new NpgsqlConnection(maintenance);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    /// <summary>
    /// The original hand-written PostgreSQL scripts, still embedded in Dispatch.Data. They are no longer
    /// used to build databases — EF does that now — but they are kept precisely so this test can construct
    /// a faithful pre-EF schema rather than an approximation of one.
    /// </summary>
    private static IEnumerable<(int Version, string Name, string Sql)> LegacyMigrations()
    {
        var asm = typeof(DispatchDbContext).Assembly;
        const string prefix = ".Migrations.Postgres.";
        var found = new List<(int, string, string)>();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            var idx = resource.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;

            var name = resource[(idx + prefix.Length)..];
            if (!int.TryParse(name.Split('_', 2)[0], out var version)) continue;

            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            found.Add((version, name, reader.ReadToEnd()));
        }

        Assert.NotEmpty(found);
        return found.OrderBy(m => m.Item1);
    }
}
