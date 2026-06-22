using Dispatch.Core.Audit;
using Dispatch.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dispatch.Data.Tests;

/// <summary>Integration tests for the audit log (System Logs). Auto-skip when DISPATCH_TEST_SQL is unset.</summary>
public class SqlAuditLogTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private SqlAuditLog NewLog() => new(sql.Factory, NullLogger<SqlAuditLog>.Instance);

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
        await using var cn = await sql.Factory.OpenAsync();

        // Seed rows with explicit ages: an old security event (10d), an old general event (100d), and a
        // fresh one. Dapper-parameterised inserts mirroring the table shape.
        async Task Seed(string kind, string category, int daysOld) =>
            await Dapper.SqlMapper.ExecuteAsync(cn,
                "INSERT INTO audit_log (logged_at, kind, category, event, severity) VALUES (DATEADD(DAY, -@d, SYSUTCDATETIME()), @k, @c, 'x', 'Info');",
                new { d = daysOld, k = kind, c = category });

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
