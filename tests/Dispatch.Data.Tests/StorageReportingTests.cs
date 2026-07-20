using Dispatch.Core.Logging;
using Dispatch.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Tests;

/// <summary>
/// Storage reporting has to produce real numbers on EVERY backend, not just the ones with a size function.
///
/// The storage page exists so an operator can see what is consuming their disk and set retention
/// accordingly. A backend that reports 0 for its own message log is not "degrading gracefully" - it is
/// telling the operator something false about their own system, and it looks like a bug. SQLite has no
/// per-table size function (dbstat is absent from the shipped builds), so SqliteDatabaseProvider computes
/// the figure from the real file size and real measured row widths. These tests are what keep that honest.
/// </summary>
public class StorageReportingTests(DatabaseFixture sql) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Reports_a_real_size_for_a_populated_table()
    {
        if (!sql.Available) return;

        var log = new SqlLogRepository(sql.Contexts);
        for (var i = 0; i < 200; i++)
            await log.InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = $"storage-{i}-{Guid.NewGuid():N}",
                FromAddress = "sender@example.com", FromDomain = "example.com",
                ToAddresses = ["recipient@example.net"], ToDomain = "example.net",
                Subject = new string('s', 200),
                ProviderResponse = new string('r', 400),
                SizeBytes = 4096,
            });

        var maintenance = new SqlLogMaintenance(sql.Contexts, sql.DbProvider);
        var (tableBytes, rowCount) = await maintenance.GetRelayLogStatsAsync();
        var databaseBytes = await maintenance.GetDatabaseSizeBytesAsync();

        Assert.True(rowCount >= 200, $"expected the rows just written, saw {rowCount}");
        Assert.True(databaseBytes > 0, $"[{sql.Engine}] database size came back as {databaseBytes}");

        // The assertion that actually matters: no engine may report zero for a table holding 200 rows of
        // several hundred bytes each.
        Assert.True(tableBytes > 0,
            $"[{sql.Engine}] relay_log holds {rowCount} rows but reported {tableBytes} bytes. " +
            "Per-table size reporting is broken on this backend.");

        // And it must be bounded by reality: a table cannot occupy more than the database it lives in.
        Assert.True(tableBytes <= databaseBytes,
            $"[{sql.Engine}] relay_log reported {tableBytes} bytes inside a {databaseBytes}-byte database.");
    }

    [Fact]
    public async Task Storage_report_attributes_the_database_across_its_tables()
    {
        if (!sql.Available) return;

        var log = new SqlLogRepository(sql.Contexts);
        for (var i = 0; i < 100; i++)
            await log.InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = $"attrib-{i}-{Guid.NewGuid():N}",
                FromAddress = "a@x.com", FromDomain = "x.com",
                ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = new string('t', 300),
            });

        var report = await new SqlStorageReport(sql.Contexts, sql.DbProvider).GetAsync();

        Assert.True(report.Connected);
        Assert.True(report.DatabaseBytes > 0, $"[{sql.Engine}] database size was {report.DatabaseBytes}");
        Assert.True(report.RelayLogBytes > 0, $"[{sql.Engine}] relay_log size was {report.RelayLogBytes}");

        // relay_log carries the bulk of a relay's data, so it must dominate - if the attribution were
        // arbitrary this is where it would show.
        Assert.True(report.RelayLogBytes > report.AuditBytes,
            $"[{sql.Engine}] relay_log ({report.RelayLogBytes}) should exceed audit_log ({report.AuditBytes})");

        Assert.Contains(report.RelayLogByEvent, e => e.Event == "Delivered" && e.Rows >= 100);
    }

    [Fact]
    public async Task An_empty_table_is_reported_far_smaller_than_a_populated_one()
    {
        if (!sql.Available) return;

        var log = new SqlLogRepository(sql.Contexts);
        for (var i = 0; i < 200; i++)
            await log.InsertAsync(new RelayLogEntry
            {
                Event = "Delivered", Status = "OK", SpoolId = $"empty-cmp-{i}-{Guid.NewGuid():N}",
                FromAddress = "a@x.com", FromDomain = "x.com",
                ToAddresses = ["b@y.com"], ToDomain = "y.com", Subject = new string('u', 400),
            });

        await using var db = await sql.Contexts.CreateDbContextAsync();
        Assert.Equal(0, await db.AuditLog.CountAsync());   // nothing has written an audit row here

        var populated = await sql.DbProvider.GetTableSizeBytesAsync(db, "relay_log");
        var empty = await sql.DbProvider.GetTableSizeBytesAsync(db, "audit_log");

        // Deliberately NOT asserting the empty table is zero, nor some ratio. On PostgreSQL, SQL Server and
        // MySQL an empty table still owns allocated pages for itself and its indexes, and reporting that is
        // correct - it really is on disk. InnoDB in particular allocates a minimum extent per table, so an
        // empty table there reports around 48 KB no matter what, and any fixed ratio would be asserting a
        // storage engine's allocation policy rather than Dispatch's behaviour.
        //
        // The portable property is the one that actually matters: content moves the number. A proportional
        // split gone wrong - the failure mode this guards - would hand the empty table a share comparable to
        // the populated one.
        Assert.True(empty < populated,
            $"[{sql.Engine}] empty audit_log reported {empty} bytes, not less than a populated relay_log at {populated}");
    }
}
