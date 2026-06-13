using Dapper;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;

namespace Dispatch.Data.Tests;

/// <summary>
/// Integration tests against a real SQL Server (Azure SQL Edge). Auto-skip when DISPATCH_TEST_SQL is unset.
/// Run with: DISPATCH_TEST_SQL="Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False" dotnet test
/// </summary>
public class SqlRepositoriesTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task Initializer_creates_schema_and_seeds_default_relay()
    {
        if (!sql.Available) return;
        await using var cn = await sql.Factory.OpenAsync();

        var version = await cn.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_version");
        Assert.Equal(1, version);

        var (name, provider, isDefault) = await cn.QuerySingleAsync<(string, string, bool)>(
            "SELECT name, provider, is_default FROM relays WHERE is_default = 1");
        Assert.Equal("default", name);
        Assert.True(isDefault);
    }

    [Fact]
    public async Task Log_insert_is_read_back_by_message_log_query()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);
        var spoolId = Guid.NewGuid().ToString("N");

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com",
            ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = "Hi",
            RelayId = 1, RelayName = "default", Provider = "None", IngestSource = "API",
        });

        var page = await query.QueryAsync(new MessageLogFilter { Statuses = ["OK"], Limit = 50 });
        Assert.Contains(page.Rows, r => r.SpoolId == spoolId && r.Provider == "None");
    }

    [Fact]
    public async Task Counter_merge_accumulates_today()
    {
        if (!sql.Available) return;
        var counters = new SqlCounterRepository(sql.Factory);

        var before = (await counters.GetTodayAsync()).Delivered;
        await counters.IncrementAsync(1, CounterField.Delivered);
        await counters.IncrementAsync(1, CounterField.Delivered);
        var after = (await counters.GetTodayAsync()).Delivered;

        Assert.Equal(before + 2, after);
    }

    [Fact]
    public async Task Counter_skips_unattributed_denied()
    {
        if (!sql.Available) return;
        var counters = new SqlCounterRepository(sql.Factory);
        // relayId 0/null has no FK target — must be a no-op, not an exception.
        await counters.IncrementAsync(0, CounterField.Denied);
        await counters.IncrementAsync(null, CounterField.Denied);
    }

    [Fact]
    public async Task ApiKey_create_verify_revoke_lifecycle()
    {
        if (!sql.Available) return;
        var keys = new SqlApiKeyRepository(sql.Factory);

        var created = await keys.CreateAsync("Test key", rateLimitPerMinute: 100);
        Assert.StartsWith("dsp_live_", created.PlaintextKey);

        var verified = await keys.VerifyAsync(created.PlaintextKey);
        Assert.NotNull(verified);
        Assert.Equal(created.Key.Id, verified!.Id);

        Assert.Null(await keys.VerifyAsync("dsp_live_totally-wrong-key-value"));

        Assert.True(await keys.RevokeAsync(created.Key.Id));
        Assert.Null(await keys.VerifyAsync(created.PlaintextKey));
    }

    [Theory]
    [InlineData("x.com'; DROP TABLE relay_log; --")]
    [InlineData("' OR '1'='1")]
    public async Task MessageLog_filters_are_injection_safe(string payload)
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
        });

        // Payload is treated as a literal value → matches nothing, injects nothing.
        var page = await query.QueryAsync(new MessageLogFilter { FromDomain = payload });
        Assert.Empty(page.Rows);

        // Table still exists and is queryable.
        await using var cn = await sql.Factory.OpenAsync();
        Assert.True(await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM relay_log") >= 1);
    }
}
