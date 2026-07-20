using Dispatch.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dispatch.Data.Tests;

/// <summary>
/// The real 0.7 upgrade: a PostgreSQL install created by the PRE-EF hand-written migrations, upgraded in
/// place and then moved onto bundled SQLite.
///
/// This is the path the one existing deployment takes, and it is not the same path as
/// <see cref="DatabaseMigratorTests"/>. That test builds its source with current code, so the source is
/// already an EF schema - which is precisely the assumption that made the first version of this upgrade
/// fail. A genuine pre-0.7 database has a `schema_version` table and no `__EFMigrationsHistory`, and the
/// migrator reads the source THROUGH the EF model, so it refused to run at all.
///
/// The fixture under Fixtures/PreEfPostgres is the actual shipped 0.6 SQL, kept so this exercises the real
/// old schema rather than a reconstruction of it. PostgreSQL only: no other engine has field deployments
/// predating 0.7.
/// </summary>
public class UpgradeFromPreEfTests
{
    private static string? BaseConnection =>
        Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL") is { Length: > 0 } s
        && !string.Equals(Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE"), "sqlite", StringComparison.OrdinalIgnoreCase)
            ? s : null;

    [Fact]
    public async Task A_pre_0_7_postgres_install_upgrades_in_place_and_migrates_to_sqlite()
    {
        if (BaseConnection is null) return;
        if (DatabaseProviderResolver.Resolve(BaseConnection, Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE"))
            != DatabaseProvider.Postgres) return;

        var dbName = "dispatch_preef_" + Guid.NewGuid().ToString("N");
        var maintenance = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = "postgres" }.ConnectionString;
        var pgConnection = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = dbName }.ConnectionString;
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"preef_{Guid.NewGuid():N}.db");
        var sqliteConnection = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ConnectionString;

        await using (var admin = new NpgsqlConnection(maintenance))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            // ---- 1. Build a database exactly as 0.6 left it: schema_version plus the shipped scripts ----
            await using (var cn = new NpgsqlConnection(pgConnection))
            {
                await cn.OpenAsync();
                await Exec(cn, """
                    CREATE TABLE schema_version (
                        version     int          NOT NULL PRIMARY KEY,
                        script_name varchar(256) NOT NULL,
                        applied_at  timestamptz  NOT NULL DEFAULT now()
                    );
                    """);

                foreach (var (version, name, sql) in PreEfMigrations())
                {
                    await Exec(cn, sql);
                    await Exec(cn, $"INSERT INTO schema_version (version, script_name) VALUES ({version}, '{name}');");
                }

                // Customer data: a relay, config (including the dashboard password hash), an API key, mail
                // history and audit rows. relay_id is read back rather than assumed - migration 0003 deletes
                // the seeded placeholder, so the first real relay does not get id 1.
                await Exec(cn, "INSERT INTO relays (name, provider, is_default, enabled) VALUES ('prod-relay', 'Smtp', true, true);");
                var relayId = await ScalarAsync(cn, "SELECT id FROM relays WHERE name = 'prod-relay';");

                await Exec(cn, """
                    INSERT INTO config ("key", value, encrypted) VALUES
                        ('smtp:banner', 'mail.customer.com', false),
                        ('auth:passwordHash', '$2a$12$notarealhashbutstable', false);
                    """);
                await Exec(cn, "INSERT INTO api_keys (key_id, key_hash, name) VALUES ('dsp_live_ab', 'hash', 'customer-key');");
                await Exec(cn, $"""
                    INSERT INTO relay_log (spool_id, event, status, from_address, from_domain, to_addresses,
                                           to_domain, subject, relay_id)
                    SELECT 'hist-'||g, 'Delivered', 'OK', 'a@customer.com', 'customer.com', '[]', 'dest.com',
                           'Historical message '||g, {relayId}
                    FROM generate_series(1, 750) g;
                    """);
                await Exec(cn, "INSERT INTO audit_log (kind, category, event, severity) SELECT 'audit', 'Config', 'change '||g, 'Info' FROM generate_series(1, 40) g;");
            }

            var contexts = DispatchDbContextFactory.Create(DatabaseProvider.Postgres, pgConnection);

            // A pre-0.7 database must be recognised as such BEFORE anything tries to migrate it.
            await using (var probe = await contexts.CreateDbContextAsync())
                Assert.True(await DatabaseInitializer.HasPreEfSchemaAsync(probe),
                    "a database with schema_version and relay_log should be detected as pre-0.7");

            // ---- 2. Upgrade in place: starting 0.7 against it adopts the schema, keeping the data --------
            await new DatabaseInitializer(
                    contexts,
                    new DatabaseBootstrap(DatabaseProvider.Postgres, pgConnection, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            await using (var db = await contexts.CreateDbContextAsync())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Equal(750, await db.RelayLog.CountAsync());          // history survived the upgrade
                Assert.Equal(2, await db.Config.CountAsync());
            }

            // ---- 3. Migrate onto bundled SQLite ----------------------------------------------------------
            await new DatabaseInitializer(
                    DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteConnection),
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, sqliteConnection, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            var result = await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).CopyAsync(
                DatabaseProvider.Postgres, pgConnection, DatabaseProvider.Sqlite, sqliteConnection);

            Assert.Equal(750, result.RowsCopied["relay_log"]);
            Assert.Equal(40, result.RowsCopied["audit_log"]);

            // ---- 4. The SQLite install is the customer's install ------------------------------------------
            var target = DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteConnection);
            await using (var db = await target.CreateDbContextAsync())
            {
                Assert.Equal(750, await db.RelayLog.CountAsync());

                // The dashboard password came across, so the operator logs in with the same credentials
                // rather than being met with first-run setup.
                var hash = await db.Config.SingleAsync(c => c.Key == "auth:passwordHash");
                Assert.Equal("$2a$12$notarealhashbutstable", hash.Value);

                // Relay ids are preserved, so historical mail stays attributed to the relay that sent it.
                var relay = await db.Relays.SingleAsync(r => r.Name == "prod-relay");
                Assert.All(await db.RelayLog.ToListAsync(), r => Assert.Equal(relay.Id, r.RelayId));
            }

            // And it is a working install: new mail lands beside the migrated history without colliding
            // with the copied keys.
            await new SqlLogRepository(target).InsertAsync(new Core.Logging.RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = "post-upgrade",
                FromAddress = "new@customer.com", FromDomain = "customer.com",
                ToAddresses = ["dest@example.net"], ToDomain = "example.net", Subject = "after the upgrade",
            });

            var page = await new SqlMessageLogQuery(target).QueryAsync(new Core.Logging.MessageLogFilter { Limit = 50 });
            Assert.Contains(page.Rows, r => r.SpoolId == "post-upgrade");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);

            await using var admin = new NpgsqlConnection(maintenance);
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task Migrating_a_pre_0_7_source_directly_explains_how_to_fix_it()
    {
        if (BaseConnection is null) return;
        if (DatabaseProviderResolver.Resolve(BaseConnection, Environment.GetEnvironmentVariable("DISPATCH_TEST_ENGINE"))
            != DatabaseProvider.Postgres) return;

        var dbName = "dispatch_preef_err_" + Guid.NewGuid().ToString("N");
        var maintenance = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = "postgres" }.ConnectionString;
        var pgConnection = new NpgsqlConnectionStringBuilder(BaseConnection) { Database = dbName }.ConnectionString;
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"preef_err_{Guid.NewGuid():N}.db");
        var sqliteConnection = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ConnectionString;

        await using (var admin = new NpgsqlConnection(maintenance))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            await using (var cn = new NpgsqlConnection(pgConnection))
            {
                await cn.OpenAsync();
                await Exec(cn, "CREATE TABLE schema_version (version int NOT NULL PRIMARY KEY, script_name varchar(256) NOT NULL);");
                foreach (var (version, name, sql) in PreEfMigrations())
                {
                    await Exec(cn, sql);
                    await Exec(cn, $"INSERT INTO schema_version (version, script_name) VALUES ({version}, '{name}');");
                }
            }

            await new DatabaseInitializer(
                    DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteConnection),
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, sqliteConnection, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            // Skipping the in-place upgrade must not produce a cryptic refusal. An operator mid-upgrade
            // needs to be told what to do next.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).CopyAsync(
                    DatabaseProvider.Postgres, pgConnection, DatabaseProvider.Sqlite, sqliteConnection));

            Assert.Contains("pre-0.7", ex.Message);
            Assert.Contains("Start the 0.7 service against it once", ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);

            await using var admin = new NpgsqlConnection(maintenance);
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    /// <summary>The shipped 0.6 PostgreSQL scripts, in order. See Fixtures/PreEfPostgres/README.md.</summary>
    private static IEnumerable<(int Version, string Name, string Sql)> PreEfMigrations()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PreEfPostgres");
        var files = Directory.GetFiles(dir, "*.sql").OrderBy(f => f).ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            yield return (int.Parse(name.Split('_', 2)[0]), name, File.ReadAllText(file));
        }
    }

    private static async Task Exec(NpgsqlConnection cn, string sql)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(NpgsqlConnection cn, string sql)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
