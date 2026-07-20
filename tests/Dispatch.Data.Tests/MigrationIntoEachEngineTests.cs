using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data.Providers;
using Dispatch.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>
/// Migration INTO whichever engine the fixture is running, with SQLite as the source.
///
/// DatabaseMigratorTests covers the direction the 0.7 upgrade needs (a server engine onto bundled SQLite).
/// This covers the other direction, which is what an operator does when they outgrow the bundled default
/// and move onto a database server they already run - and, because the fixture picks the engine, one test
/// exercises all four as targets.
///
/// SQL Server is the interesting one: it rejects an explicit value for an identity column unless
/// IDENTITY_INSERT is on for that table, and it does not advance the identity seed when one is supplied.
/// Both have to be handled or the copy fails outright, or worse, succeeds and then collides on the first
/// row written afterwards.
/// </summary>
public class MigrationIntoEachEngineTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Migrates_from_sqlite_into_this_engine_preserving_keys()
    {
        if (!sql.Available) return;

        // The fixture's database is the TARGET here, so it has to be empty - the migrator refuses to merge
        // into a database holding data, and other tests in this class share the fixture.
        await using (var check = await sql.Contexts.CreateDbContextAsync())
            if (await check.RelayLog.AnyAsync() || await check.Relays.AnyAsync() || await check.Config.AnyAsync())
                return;

        var sourcePath = Path.Combine(Path.GetTempPath(), $"into_{Guid.NewGuid():N}.db");
        var sourceCs = new SqliteConnectionStringBuilder { DataSource = sourcePath }.ConnectionString;

        try
        {
            var source = DispatchDbContextFactory.Create(DatabaseProvider.Sqlite, sourceCs);
            await new DatabaseInitializer(
                    source,
                    new DatabaseBootstrap(DatabaseProvider.Sqlite, sourceCs, NullLogger<DatabaseBootstrap>.Instance),
                    NullLogger<DatabaseInitializer>.Instance)
                .InitializeAsync();

            // Populate through the repositories, so the rows have the shape the application really writes.
            var relays = new SqlRelayRepository(source);
            var relay = await relays.CreateAsync("bundled-relay", Dispatch.Core.Providers.RelayProviderType.Local, 4, 0);
            await new SqlConfigRepository(source).SetAsync("smtp:banner", "moving.example.com");

            var keys = new SqlApiKeyRepository(source);
            var apiKey = await keys.CreateAsync("bundled-key", rateLimitPerMinute: 50);

            var log = new SqlLogRepository(source);
            var counters = new SqlCounterRepository(source, DatabaseProviders.Get(DatabaseProvider.Sqlite));
            for (var i = 0; i < 120; i++)
            {
                await log.InsertAsync(new RelayLogEntry
                {
                    Event = "Delivered", Status = "OK", SpoolId = $"into-{i:D4}",
                    FromAddress = "a@example.com", FromDomain = "example.com",
                    ToAddresses = ["b@example.net"], ToDomain = "example.net",
                    Subject = $"Bundled message {i}", RelayId = relay.Id, RelayName = relay.Name,
                    ApiKeyId = apiKey.Key.Id,
                });
                await counters.IncrementAsync(relay.Id, CounterField.Delivered);
            }

            // ---- Migrate onto the fixture's engine ----------------------------------------------------
            var result = await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).CopyAsync(
                DatabaseProvider.Sqlite, sourceCs, sql.Provider, sql.ConnectionString!);

            Assert.Equal(120, result.RowsCopied["relay_log"]);

            await using var db = await sql.Contexts.CreateDbContextAsync();

            // Keys survive. On SQL Server this only works with IDENTITY_INSERT, so a regression there shows
            // up here rather than as a foreign key pointing at a row that moved.
            var migratedRelay = await db.Relays.SingleAsync(r => r.Name == "bundled-relay");
            Assert.Equal(relay.Id, migratedRelay.Id);
            Assert.All(await db.RelayLog.ToListAsync(), r => Assert.Equal(relay.Id, r.RelayId));
            Assert.All(await db.RelayLog.ToListAsync(), r => Assert.Equal(apiKey.Key.Id, r.ApiKeyId));

            // ---- And the target is a working install ----------------------------------------------------
            // The identity seed must have been advanced past the copied rows. Without that this insert
            // collides with a key that already exists.
            var newKey = await new SqlApiKeyRepository(sql.Contexts).CreateAsync("post-move", rateLimitPerMinute: 0);
            Assert.NotEqual(apiKey.Key.Id, newKey.Key.Id);

            await new SqlLogRepository(sql.Contexts).InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = "post-move",
                FromAddress = "new@example.com", FromDomain = "example.com",
                ToAddresses = ["b@example.net"], ToDomain = "example.net", Subject = "after the move",
                RelayId = relay.Id, RelayName = relay.Name,
            });

            var page = await new SqlMessageLogQuery(sql.Contexts).QueryAsync(new MessageLogFilter { Limit = 50 });
            Assert.Contains(page.Rows, r => r.SpoolId == "post-move");

            // Counters came across and keep accumulating rather than restarting from zero.
            var totals = await new SqlCounterRepository(sql.Contexts, sql.DbProvider).GetTodayAsync();
            Assert.True(totals.Delivered >= 120, $"[{sql.Engine}] expected the 120 migrated deliveries, saw {totals.Delivered}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sourcePath + suffix);
        }
    }
}
