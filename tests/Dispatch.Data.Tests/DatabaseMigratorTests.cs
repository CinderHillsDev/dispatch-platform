using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// The 0.7 migration: moving the existing PostgreSQL install onto the bundled SQLite backend.
///
/// This runs against a real PostgreSQL server (skipped without one), populates it through the actual
/// repositories rather than hand-written INSERTs — so the data has the shape the application really
/// produces — migrates it, and then checks the things that would quietly ruin the customer's install if
/// they were wrong.
/// </summary>
public class DatabaseMigratorTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Migrates_a_populated_postgres_database_to_sqlite()
    {
        if (!sql.Available || sql.Provider != DatabaseProvider.Postgres) return;

        // ---- Populate the source the way the application would ------------------------------------
        var relays = new SqlRelayRepository(sql.Factory);
        var relay = await relays.CreateAsync("prod-relay", Dispatch.Core.Providers.RelayProviderType.Local, 8, 0);

        var config = new SqlConfigRepository(sql.Factory);
        await config.SetAsync("smtp:banner", "dispatch.example.com");
        // An encrypted value: the migration must carry ciphertext and flag across untouched.
        await config.SetAsync("relay:1:apiKey", "super-secret-token", encrypted: true);

        var keys = new SqlApiKeyRepository(sql.Factory);
        var created = await keys.CreateAsync("integration-key", rateLimitPerMinute: 250);

        var creds = new SqlSmtpCredentialRepository(sql.Factory);
        await creds.AddAsync("relay-user", "relay-password");

        var log = new SqlLogRepository(sql.Factory);
        var counters = new SqlCounterRepository(sql.Factory);
        var spoolIds = new List<string>();
        for (var i = 0; i < 250; i++)
        {
            var spoolId = $"mig-{i:D4}-{Guid.NewGuid():N}";
            spoolIds.Add(spoolId);
            await log.InsertAsync(new RelayLogEntry
            {
                Event = i % 10 == 0 ? "Failed" : "Delivered",
                Status = i % 10 == 0 ? "Error" : "OK",
                SpoolId = spoolId,
                FromAddress = $"sender{i}@example.com",
                FromDomain = "example.com",
                ToAddresses = [$"rcpt{i}@example.net", "cc@example.net"],
                ToDomain = "example.net",
                Subject = $"Historical message {i}",
                RelayId = relay.Id,
                RelayName = relay.Name,
                ApiKeyId = created.Key.Id,
                SizeBytes = 4096 + i,
            });
            await counters.IncrementAsync(relay.Id, CounterField.Delivered);
        }

        var sourceRows = await CountRelayLogAsync(sql.ConnectionString!, DatabaseProvider.Postgres);

        // ---- Migrate -------------------------------------------------------------------------------
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"dispatchmig_{Guid.NewGuid():N}.db");
        var sqliteCs = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ConnectionString;
        try
        {
            // The target must be schema-current and empty — exactly what a fresh 0.7 install looks like.
            await new DatabaseInitializer(
                    DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteCs),
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, sqliteCs, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            var result = await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).CopyAsync(
                DatabaseProvider.Postgres, sql.ConnectionString!,
                DatabaseProvider.Sqlite, sqliteCs);

            Assert.Equal(sourceRows, result.RowsCopied["relay_log"]);

            var target = DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteCs);
            await using var db = await target.CreateDbContextAsync();

            // ---- Primary keys preserved ------------------------------------------------------------
            // relay_log.id is half the Message Log's keyset cursor and the target of every FK. Renumbering
            // would break attribution on historical mail without any error surfacing.
            var relayRow = await db.Relays.SingleAsync(r => r.Name == "prod-relay");
            Assert.Equal(relay.Id, relayRow.Id);
            Assert.All(await db.RelayLog.ToListAsync(), r => Assert.Equal(relay.Id, r.RelayId));
            Assert.All(await db.RelayLog.ToListAsync(), r => Assert.Equal(created.Key.Id, r.ApiKeyId));

            // ---- Encrypted config survives as ciphertext, and still decrypts ------------------------
            var secret = await db.Config.SingleAsync(c => c.Key == "relay:1:apiKey");
            Assert.True(secret.Encrypted);
            var migratedConfig = new SqlConfigRepository(new SqlConnectionFactory(sqliteCs));
            Assert.Equal("super-secret-token", await migratedConfig.GetAsync("relay:1:apiKey"));

            // ---- Recipient JSON survives the timestamp/text round trip -----------------------------
            var sample = await db.RelayLog.OrderBy(r => r.Id).FirstAsync();
            Assert.Contains("example.net", sample.ToAddresses);

            // ---- Timestamps stay UTC and stay ordered ----------------------------------------------
            var ordered = await db.RelayLog.OrderBy(r => r.Id).Select(r => r.LoggedAt).ToListAsync();
            Assert.Equal(ordered, ordered.OrderBy(t => t).ToList());
            Assert.All(ordered, t => Assert.True(
                (DateTime.UtcNow - t).TotalHours < 24, $"timestamp {t:O} is not a recent UTC instant"));

            // ---- The migrated database is usable, and new inserts do not collide with copied keys ---
            var migratedLog = new SqlLogRepository(new SqlConnectionFactory(sqliteCs));
            await migratedLog.InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = "post-migration",
                FromAddress = "a@x.com", FromDomain = "x.com",
                ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = "after",
                RelayId = relay.Id, RelayName = relay.Name,
            });

            var query = new SqlMessageLogQuery(new SqlConnectionFactory(sqliteCs));
            var page = await query.QueryAsync(new MessageLogFilter { Limit = 50 });
            Assert.Contains(page.Rows, r => r.SpoolId == "post-migration");

            // Counters came across and still accumulate rather than restarting.
            var totals = await new SqlCounterRepository(new SqlConnectionFactory(sqliteCs)).GetTodayAsync();
            Assert.True(totals.Delivered >= 250, $"expected the 250 migrated deliveries, saw {totals.Delivered}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(sqlitePath + suffix);
        }
    }

    [Fact]
    public async Task Refuses_to_migrate_into_a_database_that_already_holds_data()
    {
        if (!sql.Available || sql.Provider != DatabaseProvider.Postgres) return;

        await new SqlConfigRepository(sql.Factory).SetAsync("guard:probe", "x");

        var sqlitePath = Path.Combine(Path.GetTempPath(), $"dispatchmig_{Guid.NewGuid():N}.db");
        var sqliteCs = new SqliteConnectionStringBuilder { DataSource = sqlitePath }.ConnectionString;
        try
        {
            await new DatabaseInitializer(
                    DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sqliteCs),
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, sqliteCs, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            // Put something in the target, so it is no longer a fresh install.
            await new SqlConfigRepository(new SqlConnectionFactory(sqliteCs)).SetAsync("existing", "data");

            var migrator = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.CopyAsync(
                DatabaseProvider.Postgres, sql.ConnectionString!,
                DatabaseProvider.Sqlite, sqliteCs));

            Assert.Contains("already contains data", ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(sqlitePath + suffix);
        }
    }

    private static async Task<int> CountRelayLogAsync(string connectionString, DatabaseProvider provider)
    {
        await using var db = await DispatchDbContextFactory.Create(provider, connectionString).CreateDbContextAsync();
        return await db.RelayLog.CountAsync();
    }
}
