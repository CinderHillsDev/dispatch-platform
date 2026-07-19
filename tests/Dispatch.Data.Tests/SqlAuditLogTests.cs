using Dispatch.Core.Audit;
using Dispatch.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>Integration tests for the audit log (System Logs). Auto-skip when DISPATCH_TEST_SQL is unset.</summary>
public class SqlAuditLogTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    private SqlAuditLog NewLog() => new(sql.Contexts, NullLogger<SqlAuditLog>.Instance);

    [Fact]
    public async Task Writes_and_queries_by_kind_and_search()
    {
        if (!sql.Available) return;
        var audit = NewLog();

        await audit.Audit("Auth", "Login failed", "Warning", actor: "admin", sourceIp: "10.0.0.1");
        await audit.Relay("Delivery failed via relay \"Maileroo\"", "401 Unauthorized");
        await audit.System("Unhandled error on /api/x", "[abc123] InvalidOperationException: boom");

        var all = await audit.QueryAsync(new AuditFilter(null, null, null, null, 50, null));
        Assert.True(all.Rows.Count >= 3);

        var relayOnly = await audit.QueryAsync(new AuditFilter("relay", null, null, null, 50, null));
        Assert.All(relayOnly.Rows, r => Assert.Equal("relay", r.Kind));
        Assert.Contains(relayOnly.Rows, r => r.Event.Contains("Maileroo"));

        var search = await audit.QueryAsync(new AuditFilter(null, null, null, "401", 50, null));
        Assert.Contains(search.Rows, r => r.Detail!.Contains("401"));
    }

    [Fact]
    public async Task Purge_respects_general_and_security_retention()
    {
        if (!sql.Available) return;
        var audit = NewLog();
        await using var db = await sql.Contexts.CreateDbContextAsync();

        // Seed rows with explicit ages: an old security event (10d), an old general event (100d), and a
        // fresh one. The age is set here rather than written as engine-specific interval arithmetic, so
        // this seeds identically on every backend.
        async Task Seed(string kind, string category, int daysOld)
        {
            db.AuditLog.Add(new AuditLogEntity
            {
                LoggedAt = DateTime.UtcNow.AddDays(-daysOld),
                Kind = kind, Category = category, Event = "x", Severity = "Info",
            });
            await db.SaveChangesAsync();
        }

        await Seed("audit", "SmtpAuth", 10);   // security, older than 7d → purged
        await Seed("audit", "Config", 100);    // general, older than 90d → purged
        await Seed("audit", "Config", 1);      // fresh → kept

        var deleted = await audit.PurgeAsync(generalRetentionDays: 90, securityRetentionDays: 7);
        Assert.True(deleted >= 2);

        var remaining = await audit.QueryAsync(new AuditFilter(null, null, null, null, 200, null));
        Assert.DoesNotContain(remaining.Rows, r => r.Category == "SmtpAuth");           // 10d security purged
        Assert.Contains(remaining.Rows, r => r.Category == "Config");                   // the fresh one survives
    }
}
