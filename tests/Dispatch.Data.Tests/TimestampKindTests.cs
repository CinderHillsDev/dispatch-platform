using Dispatch.Core.ApiKeys;
using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Tests;

/// <summary>
/// Every DateTime read back from the database must carry <see cref="DateTimeKind.Utc"/>, on every engine.
///
/// Dispatch stores UTC throughout, but only PostgreSQL round-trips that fact - timestamptz returns
/// Kind=Utc, while SQLite (ISO text), SQL Server (datetime2) and MySQL (datetime) return Kind=Unspecified
/// because their column types carry no zone. A value converter in DispatchDbContext forces Utc on read.
/// This guards that converter, because when it is missing the failure is silent and serious:
///
///   * migration INTO PostgreSQL fails outright - Npgsql refuses to write a Kind=Unspecified value to
///     timestamptz;
///   * an API response serialises a Kind=Unspecified DateTime WITHOUT the trailing Z, so the same instant
///     is emitted as "...:14" on three backends and "...:14Z" on the fourth, and a client parses two
///     different times depending on which database the operator runs.
///
/// A unit test on the converter alone would not prove the round trip through each engine's real type, so
/// this runs against the fixture's engine and asserts the Kind that actually comes back.
/// </summary>
public class TimestampKindTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Timestamps_read_back_as_utc_on_this_engine()
    {
        if (!sql.Available) return;

        // A non-null timestamp (relay_log.logged_at) and both states of a nullable one (api_keys:
        // created_at non-null, last_used_at null until used, revoked_at null until revoked).
        await new SqlLogRepository(sql.Contexts).InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = $"kind-{Guid.NewGuid():N}",
            FromAddress = "a@x.com", FromDomain = "x.com",
            ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = "kind",
        });
        ApiKeyCreated key = await new SqlApiKeyRepository(sql.Contexts).CreateAsync("kind-key", rateLimitPerMinute: 0);

        await using var db = await sql.Contexts.CreateDbContextAsync();

        var loggedAt = await db.RelayLog.AsNoTracking()
            .OrderByDescending(r => r.Id).Select(r => r.LoggedAt).FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loggedAt.Kind);

        var createdAt = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == key.Key.Id).Select(k => k.CreatedAt).SingleAsync();
        Assert.Equal(DateTimeKind.Utc, createdAt.Kind);

        // A nullable timestamp that HAS a value must also come back Utc; one that is null must stay null
        // (the nullable converter must not turn null into a Kind-stamped default).
        var apiRow = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Key.Id);
        Assert.Null(apiRow.LastUsedAt);
        Assert.Null(apiRow.RevokedAt);

        // Now give the nullable one a value and confirm it round-trips as Utc.
        await new SqlApiKeyRepository(sql.Contexts).RevokeAsync(key.Key.Id);
        var revokedAt = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == key.Key.Id).Select(k => k.RevokedAt).SingleAsync();
        Assert.NotNull(revokedAt);
        Assert.Equal(DateTimeKind.Utc, revokedAt!.Value.Kind);
    }
}
