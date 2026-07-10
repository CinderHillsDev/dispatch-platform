using Dapper;
using Dispatch.Core.Logging;
using Npgsql;

namespace Dispatch.Data.Repositories;

/// <summary>Probes the database with a short-budget <c>SELECT 1</c>; reports unreachable instead of hanging /health.</summary>
public sealed class SqlDatabaseHealth(SqlConnectionFactory factory) : IDatabaseHealth
{
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        // Cap the connect string's connect timeout so a down database fails fast.
        var cs = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Timeout = 2 }.ConnectionString;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var cn = new NpgsqlConnection(cs);
            await cn.OpenAsync(cts.Token);
            await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cts.Token));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
