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

        // All embedded migrations applied (0001_init, 0002_relay_log_indexes, …).
        var version = await cn.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_version");
        Assert.True(version >= 2, $"expected at least migration 2 applied, got {version}");

        var (name, provider, isDefault) = await cn.QuerySingleAsync<(string, string, bool)>(
            "SELECT name, provider, is_default FROM relays WHERE is_default = 1");
        Assert.Equal("default", name);
        Assert.True(isDefault);
    }

    [Fact]
    public async Task Default_relay_is_unconfigured_until_a_provider_is_set()
    {
        if (!sql.Available) return;
        var settings = new SqlRelaySettingsStore(new SqlConfigRepository(sql.Factory));

        // Fresh DB: the seeded default relay has no provider configured.
        var fresh = await settings.GetAsync(1);
        Assert.Equal(Dispatch.Core.Providers.RelayProviderType.Unconfigured, fresh.Provider);

        await settings.SaveAsync(1, new Dispatch.Core.Relays.RelaySettings(
            Dispatch.Core.Providers.RelayProviderType.Local, new Dictionary<string, string?>()));
        var after = await settings.GetAsync(1);
        Assert.Equal(Dispatch.Core.Providers.RelayProviderType.Local, after.Provider);
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
    public async Task Log_retention_purge_deletes_aged_rows()
    {
        if (!sql.Available) return;
        var maintenance = new SqlLogMaintenance(sql.Factory);
        var spoolId = Guid.NewGuid().ToString("N");

        // Insert a Delivered row dated 100 days ago (bypassing the SYSUTCDATETIME default).
        await using (var cn = await sql.Factory.OpenAsync())
        {
            await cn.ExecuteAsync("""
                INSERT INTO relay_log (logged_at, spool_id, event, status, from_address, from_domain, to_addresses, to_domain, subject)
                VALUES (DATEADD(DAY, -100, SYSUTCDATETIME()), @spoolId, 'Delivered', 'OK', 'a@x.com', 'x.com', '[]', 'y.com', 's');
                """, new { spoolId });
        }

        var deleted = await maintenance.PurgeByRetentionAsync("Delivered", retentionDays: 30);
        Assert.True(deleted >= 1);

        await using var verify = await sql.Factory.OpenAsync();
        var remaining = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM relay_log WHERE spool_id = @spoolId", new { spoolId });
        Assert.Equal(0, remaining);

        Assert.True(await maintenance.GetDatabaseSizeBytesAsync() > 0);
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
    public async Task Smtp_credential_add_verify_delete()
    {
        if (!sql.Available) return;
        var creds = new SqlSmtpCredentialRepository(sql.Factory);

        await creds.AddAsync("sender1", "s3cret-pass");
        Assert.True(await creds.VerifyAsync("sender1", "s3cret-pass"));
        Assert.False(await creds.VerifyAsync("sender1", "wrong"));
        Assert.False(await creds.VerifyAsync("ghost", "whatever"));
        Assert.Contains(await creds.ListAsync(), c => c.Username == "sender1");

        Assert.True(await creds.DeleteAsync("sender1"));
        Assert.False(await creds.VerifyAsync("sender1", "s3cret-pass"));
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

    [Fact]
    public async Task Log_row_with_api_key_is_returned_by_per_key_query()
    {
        if (!sql.Available) return;
        var keys = new SqlApiKeyRepository(sql.Factory);
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);

        // Two distinct keys so we can assert scoping (api_key_id has an FK to api_keys).
        var keyA = (await keys.CreateAsync("per-key A", rateLimitPerMinute: 0)).Key;
        var keyB = (await keys.CreateAsync("per-key B", rateLimitPerMinute: 0)).Key;

        var spoolOk = Guid.NewGuid().ToString("N");
        var spoolErr = Guid.NewGuid().ToString("N");

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolOk,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
            IngestSource = "API", ApiKeyId = keyA.Id, ApiKeyName = keyA.Name, Provider = "None",
        });
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Failed", Status = "Error", SpoolId = spoolErr,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
            IngestSource = "API", ApiKeyId = keyA.Id, ApiKeyName = keyA.Name, Error = "boom",
        });
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
            IngestSource = "API", ApiKeyId = keyB.Id, ApiKeyName = keyB.Name,
        });

        // Key A sees both of its rows, never key B's.
        var forA = await query.RecentByApiKeyAsync(keyA.Id, limit: 50, statuses: null);
        Assert.Contains(forA, r => r.SpoolId == spoolOk);
        Assert.Contains(forA, r => r.SpoolId == spoolErr);
        Assert.Equal(2, forA.Count(r => r.SpoolId == spoolOk || r.SpoolId == spoolErr));

        // Status filter narrows to the matching row only.
        var failedOnly = await query.RecentByApiKeyAsync(keyA.Id, limit: 50, statuses: ["Error"]);
        Assert.Contains(failedOnly, r => r.SpoolId == spoolErr);
        Assert.DoesNotContain(failedOnly, r => r.SpoolId == spoolOk);

        // The detail projection carries the api key name through.
        var detail = await query.GetByIdAsync(forA.First(r => r.SpoolId == spoolOk).Id);
        Assert.Equal(keyA.Name, detail!.ApiKeyName);
    }

    [Fact]
    public async Task MessageLog_keyset_pagination_walks_all_rows_once()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);
        var domain = "page-" + Guid.NewGuid().ToString("N")[..8] + ".test";

        for (var i = 0; i < 3; i++)
            await log.InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
                FromAddress = $"u{i}@x.com", FromDomain = "x.com", ToAddresses = [$"a@{domain}"], ToDomain = domain,
            });

        var page1 = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Limit = 2 });
        Assert.Equal(2, page1.Rows.Count);
        Assert.NotNull(page1.NextCursor);

        var page2 = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Limit = 2, Cursor = page1.NextCursor });
        Assert.Single(page2.Rows);
        Assert.Null(page2.NextCursor);

        var ids = page1.Rows.Concat(page2.Rows).Select(r => r.Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());   // no overlap, all walked once
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

    [Fact]
    public async Task MessageLog_relay_and_tag_filters_match_only_intended_rows()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);
        var domain = "ftag-" + Guid.NewGuid().ToString("N")[..8] + ".test";

        var taggedSpool = Guid.NewGuid().ToString("N");
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = taggedSpool,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "alpha", Tags = ["urgent", "newsletter"],
        });
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "beta", Tags = ["routine"],
        });

        var byRelay = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, RelayName = "alpha" });
        Assert.Single(byRelay.Rows);
        Assert.Equal(taggedSpool, byRelay.Rows[0].SpoolId);

        var byTag = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Tag = "urgent" });
        Assert.Single(byTag.Rows);
        Assert.Equal(taggedSpool, byTag.Rows[0].SpoolId);

        // Tag filter must be a literal value, not a SQL fragment / wildcard injection.
        var noMatch = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Tag = "urgent\"; DROP TABLE relay_log; --" });
        Assert.Empty(noMatch.Rows);
    }

    [Fact]
    public async Task MessageLog_GetByIdAsync_returns_full_detail_with_parsed_arrays()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);
        var spoolId = Guid.NewGuid().ToString("N");

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com",
            ToAddresses = ["b@y.com", "c@y.com"], ToDomain = "y.com", Subject = "Hi",
            RelayId = 1, RelayName = "default", RoutingRuleName = "rule-1", RoutingMatched = true,
            Provider = "None", ProviderMessageId = "pm-123", ProviderResponse = "250 OK",
            IngestSource = "API", SourceIp = "10.0.0.5", Tags = ["urgent", "vip"],
        });

        // Find the row id we just inserted.
        await using var cn = await sql.Factory.OpenAsync();
        var id = await cn.ExecuteScalarAsync<long>(
            "SELECT id FROM relay_log WHERE spool_id = @spoolId", new { spoolId });

        var detail = await query.GetByIdAsync(id);
        Assert.NotNull(detail);
        Assert.Equal(spoolId, detail!.SpoolId);
        Assert.Equal(["b@y.com", "c@y.com"], detail.ToAddresses);
        Assert.Equal(["urgent", "vip"], detail.Tags);
        Assert.Equal("rule-1", detail.RoutingRuleName);
        Assert.True(detail.RoutingMatched);
        Assert.Equal("pm-123", detail.ProviderMessageId);
        Assert.Equal("250 OK", detail.ProviderResponse);
        Assert.Equal("10.0.0.5", detail.SourceIp);

        Assert.Null(await query.GetByIdAsync(-1));
    }

    [Fact]
    public async Task MessageLog_routing_rule_filter_and_retry_history()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Factory);
        var query = new SqlMessageLogQuery(sql.Factory);
        var domain = "rule-" + Guid.NewGuid().ToString("N")[..8] + ".test";
        var ruleName = "rule-" + Guid.NewGuid().ToString("N")[..8];
        var spoolId = Guid.NewGuid().ToString("N");

        // Two attempts for the same spool id (a retry then a delivery), both routed by the named rule.
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Retrying", Status = "Error", SpoolId = spoolId, RetryAttempt = 1, Error = "upstream 421",
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "alpha", RoutingRuleName = ruleName, RoutingMatched = true, Provider = "None",
        });
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId, DurationMs = 12,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "alpha", RoutingRuleName = ruleName, RoutingMatched = true, Provider = "None",
        });
        // A different message NOT routed by the rule — must be excluded by the rule filter.
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "beta",
        });

        var byRule = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, RoutingRuleName = ruleName });
        Assert.Equal(2, byRule.Rows.Count);
        Assert.All(byRule.Rows, r => Assert.Equal(spoolId, r.SpoolId));

        // Subject substring filter: matches case-insensitively; LIKE wildcards in the value are literal.
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"), Subject = "Invoice #4242 ready",
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain, RelayName = "alpha",
        });
        var bySubject = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Subject = "invoice" });
        Assert.Single(bySubject.Rows);
        Assert.Contains("Invoice", bySubject.Rows[0].Subject);
        var noSubject = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Subject = "%nope%" });
        Assert.Empty(noSubject.Rows);

        // GetByIdAsync includes the full attempt timeline for the spool id, oldest first.
        var detail = await query.GetByIdAsync(byRule.Rows[0].Id);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.History.Count);
        Assert.Equal("Retrying", detail.History[0].Event);
        Assert.Equal("Delivered", detail.History[1].Event);
    }
}
