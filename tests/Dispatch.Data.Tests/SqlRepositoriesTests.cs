using Microsoft.EntityFrameworkCore;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;

namespace Dispatch.Data.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL server. Auto-skip when DISPATCH_TEST_SQL is unset.
/// Run with: DISPATCH_TEST_SQL="Host=localhost;Port=5432;Username=postgres;Password=..." dotnet test
/// </summary>
public class SqlRepositoriesTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    // No relay is seeded any more (migration 0003 removed the placeholder), and relay_log/relay_counters
    // FK to relays(id), so tests that attribute rows to a relay create one first and use its id.
    private async Task<int> NewRelayAsync(string name = "test")
    {
        var relays = new SqlRelayRepository(sql.Contexts);
        var r = await relays.CreateAsync($"{name}-{Guid.NewGuid():N}", Dispatch.Core.Providers.RelayProviderType.Local, 4, 0);
        return r.Id;
    }

    [Fact]
    public async Task Initializer_creates_schema_without_unconfigured_placeholder()
    {
        if (!sql.Available) return;

        // The schema is current: every migration in this provider's assembly has been applied and none is
        // pending. (Versions used to be tracked in a hand-rolled schema_version table; EF records them in
        // __EFMigrationsHistory, so this asks the initializer rather than reading the table directly.)
        await using (var db = await sql.Contexts.CreateDbContextAsync())
        {
            var applied = await db.Database.GetAppliedMigrationsAsync();
            var pending = await db.Database.GetPendingMigrationsAsync();
            Assert.NotEmpty(applied);
            Assert.Empty(pending);
        }

        // The old 0001 seeded an "Unconfigured" placeholder relay that 0003 then removed, because an empty
        // relay you must go and edit confused first-run users. The first-run wizard creates the first real
        // relay instead, and that becomes the catch-all. No placeholder should exist on a fresh database.
        await using var probe = await sql.Contexts.CreateDbContextAsync();
        Assert.Equal(0, await probe.Relays.CountAsync(r => r.Provider == "Unconfigured" && r.IsDefault));
    }

    [Fact]
    public async Task Denied_counter_is_recorded_in_totals_but_excluded_from_per_relay()
    {
        if (!sql.Available) return;
        var counters = new SqlCounterRepository(sql.Contexts, sql.DbProvider);

        // A connection-level denial has no relay (relay_id 0). Before migration 0007 this was dropped by the
        // NOT NULL FK, so denials never reached /stats or Reports even though relay_log recorded them.
        var before = (await counters.GetTodayAsync()).Denied;
        await counters.IncrementAsync(0, CounterField.Denied);
        var after = (await counters.GetTodayAsync()).Denied;
        Assert.Equal(before + 1, after);

        // Range totals (the Reports summary) include it too.
        var today = DateTime.UtcNow.Date;
        Assert.True((await counters.GetRangeTotalsAsync(today, today)).Denied >= 1);

        // ...but the per-relay breakdown must not surface the relay_id 0 bucket as a phantom relay.
        Assert.DoesNotContain(await counters.GetTodayByRelayAsync(), r => r.RelayId == 0);
    }

    [Fact]
    public async Task Default_relay_is_unconfigured_until_a_provider_is_set()
    {
        if (!sql.Available) return;
        var settings = new SqlRelaySettingsStore(new SqlConfigRepository(sql.Contexts));

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
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
        var spoolId = Guid.NewGuid().ToString("N");
        var relayId = await NewRelayAsync();

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com",
            ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = "Hi",
            RelayId = relayId, RelayName = "default", Provider = "None", IngestSource = "API",
        });

        var page = await query.QueryAsync(new MessageLogFilter { Statuses = ["OK"], Limit = 50 });
        Assert.Contains(page.Rows, r => r.SpoolId == spoolId && r.Provider == "None");
    }

    [Fact]
    public async Task Log_retention_purge_deletes_aged_rows()
    {
        if (!sql.Available) return;
        var maintenance = new SqlLogMaintenance(sql.Contexts, sql.DbProvider);
        var spoolId = Guid.NewGuid().ToString("N");

        // Insert a Delivered row dated 100 days ago, overriding the timestamp default. The age is set here
        // rather than written as engine-specific interval arithmetic.
        await using (var db = await sql.Contexts.CreateDbContextAsync())
        {
            db.RelayLog.Add(new RelayLogEntity
            {
                LoggedAt = DateTime.UtcNow.AddDays(-100),
                SpoolId = spoolId, Event = "Delivered", Status = "OK",
                FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = "[]", ToDomain = "y.com",
                Subject = "s",
            });
            await db.SaveChangesAsync();
        }

        var deleted = await maintenance.PurgeByRetentionAsync("Delivered", retentionDays: 30);
        Assert.True(deleted >= 1);

        await using var verify = await sql.Contexts.CreateDbContextAsync();
        Assert.Equal(0, await verify.RelayLog.CountAsync(r => r.SpoolId == spoolId));

        Assert.True(await maintenance.GetDatabaseSizeBytesAsync() > 0);
    }

    [Fact]
    public async Task Counter_merge_accumulates_today()
    {
        if (!sql.Available) return;
        var counters = new SqlCounterRepository(sql.Contexts, sql.DbProvider);
        var relayId = await NewRelayAsync();

        var before = (await counters.GetTodayAsync()).Delivered;
        await counters.IncrementAsync(relayId, CounterField.Delivered);
        await counters.IncrementAsync(relayId, CounterField.Delivered);
        var after = (await counters.GetTodayAsync()).Delivered;

        Assert.Equal(before + 2, after);
    }

    [Fact]
    public async Task Counter_skips_unattributed_denied()
    {
        if (!sql.Available) return;
        var counters = new SqlCounterRepository(sql.Contexts, sql.DbProvider);
        // relayId 0/null has no FK target - must be a no-op, not an exception.
        await counters.IncrementAsync(0, CounterField.Denied);
        await counters.IncrementAsync(null, CounterField.Denied);
    }

    [Fact]
    public async Task Smtp_credential_add_verify_delete()
    {
        if (!sql.Available) return;
        var creds = new SqlSmtpCredentialRepository(sql.Contexts);

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
        var keys = new SqlApiKeyRepository(sql.Contexts);

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
    public async Task Message_log_list_collapses_lifecycle_events_to_one_row_per_message()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);

        var dom = $"grp-{Guid.NewGuid():N}.test";   // unique domain so the query is scoped to this test's rows
        var spool = Guid.NewGuid().ToString("N");

        // One message that retried twice then delivered = 3 relay_log rows sharing one spool id.
        await log.InsertAsync(new RelayLogEntry { Event = "Retrying", Status = "Error", SpoolId = spool, FromAddress = $"a@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com", Provider = "None", Error = "temp" });
        await log.InsertAsync(new RelayLogEntry { Event = "Retrying", Status = "Error", SpoolId = spool, FromAddress = $"a@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com", Provider = "None", Error = "temp" });
        await log.InsertAsync(new RelayLogEntry { Event = "Delivered", Status = "OK", SpoolId = spool, FromAddress = $"a@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com", Provider = "None" });
        // TWO connection-level denials. Denials have no spool id, and each is its OWN message - they must
        // never collapse together the way lifecycle rows sharing a spool id do. Two, not one, so a dedup
        // that keyed only on spool_id would visibly merge them and fail here.
        await log.InsertAsync(new RelayLogEntry { Event = "Denied", Status = "Denied", SpoolId = "", FromAddress = $"c@{dom}", FromDomain = dom, ToAddresses = [], ToDomain = "", IngestSource = "SMTP", Error = "blocked" });
        await log.InsertAsync(new RelayLogEntry { Event = "Denied", Status = "Denied", SpoolId = "", FromAddress = $"d@{dom}", FromDomain = dom, ToAddresses = [], ToDomain = "", IngestSource = "SMTP", Error = "blocked" });

        var page = await query.PageAsync(new MessageLogFilter { FromDomain = dom, Limit = 50 }, offset: 0);

        // The 3 lifecycle rows collapse to ONE (latest event = Delivered); each denial stays its own row.
        Assert.Equal(3, page.Total);
        Assert.Equal("Delivered", page.Rows.Single(r => r.SpoolId == spool).Event);
        Assert.Equal(2, page.Rows.Count(r => r.Event == "Denied"));
    }

    [Fact]
    public async Task Message_log_offset_paging_walks_the_deduped_set_without_gaps_or_repeats()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);

        // Five messages under one scope: three simple, one that retried (two rows, one message), and one
        // anonymous denial. Deduped that is five list entries - enough to page through with a small limit.
        var dom = $"pg-{Guid.NewGuid():N}.test";
        for (var i = 0; i < 3; i++)
            await log.InsertAsync(new RelayLogEntry { Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"), FromAddress = $"s{i}@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com" });
        var retried = Guid.NewGuid().ToString("N");
        await log.InsertAsync(new RelayLogEntry { Event = "Retrying", Status = "Error", SpoolId = retried, FromAddress = $"r@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com", Provider = "None", Error = "temp" });
        await log.InsertAsync(new RelayLogEntry { Event = "Delivered", Status = "OK", SpoolId = retried, FromAddress = $"r@{dom}", FromDomain = dom, ToAddresses = ["b@y.com"], ToDomain = "y.com", Provider = "None" });
        await log.InsertAsync(new RelayLogEntry { Event = "Denied", Status = "Denied", SpoolId = "", FromAddress = $"d@{dom}", FromDomain = dom, ToAddresses = [], ToDomain = "", IngestSource = "SMTP", Error = "blocked" });

        var filter = new MessageLogFilter { FromDomain = dom, Limit = 50 };
        var whole = await query.PageAsync(filter, offset: 0);
        Assert.Equal(5, whole.Total);

        // Walking the list two at a time must reproduce the single-page ordering exactly - same ids, same
        // order, every row once. This is the offset-JOIN path (Skip/Take over the id-set join); a plan that
        // reordered or double-counted under offset would diverge here.
        var walked = new List<long>();
        for (var offset = 0; offset < whole.Total; offset += 2)
        {
            var slice = await query.PageAsync(new MessageLogFilter { FromDomain = dom, Limit = 2 }, offset);
            Assert.Equal(5, slice.Total);   // total is independent of the page window
            walked.AddRange(slice.Rows.Select(r => r.Id));
        }

        Assert.Equal(whole.Rows.Select(r => r.Id), walked);            // same sequence, in order
        Assert.Equal(walked.Count, walked.Distinct().Count());          // no row served twice
    }

    [Fact]
    public async Task Log_row_with_api_key_is_returned_by_per_key_query()
    {
        if (!sql.Available) return;
        var keys = new SqlApiKeyRepository(sql.Contexts);
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);

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
    public async Task GetBySpoolId_is_scoped_to_the_calling_key()
    {
        if (!sql.Available) return;
        var keys = new SqlApiKeyRepository(sql.Contexts);
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);

        var keyA = (await keys.CreateAsync("spoolid A", rateLimitPerMinute: 0)).Key;
        var keyB = (await keys.CreateAsync("spoolid B", rateLimitPerMinute: 0)).Key;
        var spoolId = Guid.NewGuid().ToString("N");

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
            IngestSource = "API", ApiKeyId = keyA.Id, ApiKeyName = keyA.Name, Provider = "Local",
        });

        // The owning key sees it; another key must not (no cross-key status/provider leak).
        Assert.NotNull(await query.GetBySpoolIdAsync(spoolId, keyA.Id));
        Assert.Null(await query.GetBySpoolIdAsync(spoolId, keyB.Id));
        // A null key id means "no scoping" (internal/admin callers) and still resolves the row.
        Assert.NotNull(await query.GetBySpoolIdAsync(spoolId, null));
    }

    [Fact]
    public async Task MessageLog_all_filter_fields_are_injection_safe()
    {
        // Complements the FromDomain [Theory] above by exercising the other filter fields, including the
        // LIKE-based Subject/Tag matches (the more interesting injection surfaces).
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
        var spoolId = Guid.NewGuid().ToString("N");

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
            Subject = "legit", IngestSource = "API", RelayName = "default", Provider = "Local",
            Tags = ["welcome"],
        });

        // Filters are composed as LINQ predicates (spec §17), so these payloads are bound as parameters
        // text: they must not error and must not match the real row - and the table must survive.
        const string drop = "x'; DROP TABLE relay_log; --";
        MessageLogFilter[] hostile =
        [
            new() { FromDomain = drop, Limit = 50 },
            new() { ToDomain = drop, Limit = 50 },
            new() { RelayName = drop, Limit = 50 },
            new() { IngestSource = drop, Limit = 50 },
            new() { Subject = "legit'; DELETE FROM relay_log; --", Limit = 50 },
            new() { Tag = "welcome\"); DROP TABLE relay_log; --", Limit = 50 },
        ];
        foreach (var f in hostile)
        {
            var page = await query.QueryAsync(f);                       // must not throw
            Assert.DoesNotContain(page.Rows, r => r.SpoolId == spoolId); // literal value → no match
        }

        // The table is intact and the row is still retrievable with a legitimate filter.
        var ok = await query.QueryAsync(new MessageLogFilter { FromDomain = "x.com", Limit = 50 });
        Assert.Contains(ok.Rows, r => r.SpoolId == spoolId);
    }

    [Fact]
    public async Task MessageLog_keyset_pagination_walks_all_rows_once()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
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
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@y.com"], ToDomain = "y.com",
        });

        // Payload is treated as a literal value → matches nothing, injects nothing.
        var page = await query.QueryAsync(new MessageLogFilter { FromDomain = payload });
        Assert.Empty(page.Rows);

        // Table still exists and is queryable.
        await using var db = await sql.Contexts.CreateDbContextAsync();
        Assert.True(await db.RelayLog.CountAsync() >= 1);
    }

    [Fact]
    public async Task MessageLog_relay_and_tag_filters_match_only_intended_rows()
    {
        if (!sql.Available) return;
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
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
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
        var spoolId = Guid.NewGuid().ToString("N");
        var relayId = await NewRelayAsync();

        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = spoolId,
            FromAddress = "a@x.com", FromDomain = "x.com",
            ToAddresses = ["b@y.com", "c@y.com"], ToDomain = "y.com", Subject = "Hi",
            RelayId = relayId, RelayName = "default", RoutingRuleName = "rule-1", RoutingMatched = true,
            Provider = "None", ProviderMessageId = "pm-123", ProviderResponse = "250 OK",
            IngestSource = "API", SourceIp = "10.0.0.5", Tags = ["urgent", "vip"],
        });

        // Find the row id we just inserted.
        await using var db = await sql.Contexts.CreateDbContextAsync();
        var id = await db.RelayLog.Where(r => r.SpoolId == spoolId).Select(r => r.Id).SingleAsync();

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
        var log = new SqlLogRepository(sql.Contexts);
        var query = new SqlMessageLogQuery(sql.Contexts);
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
        // A different message NOT routed by the rule - must be excluded by the rule filter.
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"),
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain,
            RelayName = "beta",
        });

        var byRule = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, RoutingRuleName = ruleName });
        Assert.Equal(2, byRule.Rows.Count);
        Assert.All(byRule.Rows, r => Assert.Equal(spoolId, r.SpoolId));

        // Subject substring filter: case-sensitive (PostgreSQL LIKE); LIKE wildcards in the value are literal.
        await log.InsertAsync(new RelayLogEntry
        {
            Event = "Delivered", Status = "OK", SpoolId = Guid.NewGuid().ToString("N"), Subject = "Invoice #4242 ready",
            FromAddress = "a@x.com", FromDomain = "x.com", ToAddresses = ["b@" + domain], ToDomain = domain, RelayName = "alpha",
        });
        var bySubject = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Subject = "Invoice" });
        Assert.Single(bySubject.Rows);
        Assert.Contains("Invoice", bySubject.Rows[0].Subject);
        // Case-sensitive: the wrong case does not match.
        var wrongCase = await query.QueryAsync(new MessageLogFilter { ToDomain = domain, Subject = "invoice" });
        Assert.Empty(wrongCase.Rows);
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
