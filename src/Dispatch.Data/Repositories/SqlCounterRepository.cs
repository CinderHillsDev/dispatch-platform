using Dapper;
using Dispatch.Core.Counters;

namespace Dispatch.Data.Repositories;

/// <summary>Always-written daily aggregates via MERGE upsert (spec §6.11). Never on the hot path.</summary>
public sealed class SqlCounterRepository(SqlConnectionFactory factory) : ICounterRepository, ICounterReader
{
    public async Task IncrementAsync(int? relayId, CounterField field, CancellationToken ct = default)
    {
        // relay_counters.relay_id is a NOT NULL FK; connection-level denials (no relay) are skipped here
        // and surfaced via relay_log instead.
        if (relayId is null or <= 0)
            return;

        var column = field switch
        {
            CounterField.Received => "received",
            CounterField.Delivered => "delivered",
            CounterField.Failed => "failed",
            CounterField.Retried => "retried",
            CounterField.Denied => "denied",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        // Column name comes from a fixed enum mapping, never user input.
        var sql = $"""
            MERGE relay_counters AS t
            USING (VALUES (CAST(SYSUTCDATETIME() AS DATE), @relayId)) AS s(date, relay_id)
                ON t.date = s.date AND t.relay_id = s.relay_id
            WHEN MATCHED THEN UPDATE SET {column} = {column} + 1
            WHEN NOT MATCHED THEN INSERT (date, relay_id, {column}) VALUES (s.date, s.relay_id, 1);
            """;

        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(sql, new { relayId }, cancellationToken: ct));
    }

    public async Task<CounterTotals> GetTodayAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                ISNULL(SUM(received), 0)  AS Received,
                ISNULL(SUM(delivered), 0) AS Delivered,
                ISNULL(SUM(failed), 0)    AS Failed,
                ISNULL(SUM(retried), 0)   AS Retried,
                ISNULL(SUM(denied), 0)    AS Denied
            FROM relay_counters
            WHERE date = CAST(SYSUTCDATETIME() AS DATE);
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleAsync<CounterTotals>(new CommandDefinition(sql, cancellationToken: ct));
    }
}
