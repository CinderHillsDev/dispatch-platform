using Microsoft.Data.SqlClient;

namespace Dispatch.Data;

/// <summary>Creates SQL connections from the configured connection string.</summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public SqlConnection Create() => new(ConnectionString);

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }
}
