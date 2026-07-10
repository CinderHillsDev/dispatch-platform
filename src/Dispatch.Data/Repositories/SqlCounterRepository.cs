using Dapper;
using Dispatch.Core.Counters;

namespace Dispatch.Data.Repositories;

/// <summary>Always-written daily aggregates via MERGE upsert (spec §6.11). Never on the hot path.</summary>
public sealed class SqlCounterRepository(SqlConnectionFactory factory) : ICounterRepository, ICounterReader
{
    public async Task IncrementAsync(int? relayId, CounterField field, CancellationToken ct = default)
    {
        // relay_id 0 is the "no specific relay" bucket for connection-level events (denials counted before
        // routing). It's summed into the totals but excluded from per-relay views. Negative ids are invalid.
        var rid = relayId ?? 0;
        if (rid < 0)
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
            INSERT INTO relay_counters (date, relay_id, {column}) VALUES (CURRENT_DATE, @relayId, 1)
            ON CONFLICT (date, relay_id) DO UPDATE SET {column} = relay_counters.{column} + 1;
            """;

        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(sql, new { relayId = rid }, cancellationToken: ct));
    }

    public async Task<CounterTotals> GetTodayAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COALESCE(SUM(received), 0)::bigint  AS Received,
                COALESCE(SUM(delivered), 0)::bigint AS Delivered,
                COALESCE(SUM(failed), 0)::bigint    AS Failed,
                COALESCE(SUM(retried), 0)::bigint   AS Retried,
                COALESCE(SUM(denied), 0)::bigint    AS Denied
            FROM relay_counters
            WHERE date = CURRENT_DATE;
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleAsync<CounterTotals>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RelayCounterTotals>> GetTodayByRelayAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT relay_id AS RelayId,
                   COALESCE(SUM(received), 0)::bigint  AS Received,
                   COALESCE(SUM(delivered), 0)::bigint AS Delivered,
                   COALESCE(SUM(failed), 0)::bigint    AS Failed,
                   COALESCE(SUM(retried), 0)::bigint   AS Retried,
                   COALESCE(SUM(denied), 0)::bigint    AS Denied
            FROM relay_counters
            WHERE date = CURRENT_DATE
              AND relay_id > 0              -- exclude the relay_id 0 "no relay" bucket (denials) from per-relay views
            GROUP BY relay_id;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<RelayCounterTotals>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<CounterTotals> GetRangeTotalsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COALESCE(SUM(received), 0)::bigint  AS Received,
                COALESCE(SUM(delivered), 0)::bigint AS Delivered,
                COALESCE(SUM(failed), 0)::bigint    AS Failed,
                COALESCE(SUM(retried), 0)::bigint   AS Retried,
                COALESCE(SUM(denied), 0)::bigint    AS Denied
            FROM relay_counters
            WHERE date >= @From AND date <= @To;
            """;
        await using var cn = await factory.OpenAsync(ct);
        return await cn.QuerySingleAsync<CounterTotals>(new CommandDefinition(sql, new { From = fromUtc.Date, To = toUtc.Date }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DailyCounterTotals>> GetDailyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT to_char(date, 'YYYY-MM-DD') AS Date,
                   COALESCE(SUM(received), 0)::bigint  AS Received,
                   COALESCE(SUM(delivered), 0)::bigint AS Delivered,
                   COALESCE(SUM(failed), 0)::bigint    AS Failed,
                   COALESCE(SUM(retried), 0)::bigint   AS Retried,
                   COALESCE(SUM(denied), 0)::bigint    AS Denied
            FROM relay_counters
            WHERE date >= @From AND date <= @To
            GROUP BY date
            ORDER BY date ASC;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<DailyCounterTotals>(new CommandDefinition(sql, new { From = fromUtc.Date, To = toUtc.Date }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RelayReportRow>> GetRangeByRelayAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT c.relay_id AS RelayId, r.name AS RelayName,
                   COALESCE(SUM(c.received), 0)::bigint  AS Received,
                   COALESCE(SUM(c.delivered), 0)::bigint AS Delivered,
                   COALESCE(SUM(c.failed), 0)::bigint    AS Failed,
                   COALESCE(SUM(c.retried), 0)::bigint   AS Retried,
                   COALESCE(SUM(c.denied), 0)::bigint    AS Denied
            FROM relay_counters c
            JOIN relays r ON r.id = c.relay_id
            WHERE c.date >= @From AND c.date <= @To
            GROUP BY c.relay_id, r.name
            ORDER BY SUM(c.received) DESC, SUM(c.delivered) DESC;
            """;
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<RelayReportRow>(new CommandDefinition(sql, new { From = fromUtc.Date, To = toUtc.Date }, cancellationToken: ct));
        return rows.ToList();
    }
}
