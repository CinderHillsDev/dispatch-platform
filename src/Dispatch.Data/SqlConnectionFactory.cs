using Npgsql;

namespace Dispatch.Data;

/// <summary>Creates PostgreSQL connections from the configured connection string.</summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public NpgsqlConnection Create() => new(ConnectionString);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new NpgsqlConnection(ConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }
}
