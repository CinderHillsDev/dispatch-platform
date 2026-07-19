using System.Data.Common;
using Dispatch.Data.Dialects;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Creates connections to the configured database, whichever engine backs it. Every repository takes its
/// connections from here and reaches engine-specific SQL only through <see cref="Dialect"/>.
/// </summary>
public sealed class SqlConnectionFactory
{
    public SqlConnectionFactory(string connectionString, ISqlDialect dialect)
    {
        ConnectionString = connectionString;
        Dialect = dialect;
    }

    public SqlConnectionFactory(string connectionString, ILogger? log = null)
        : this(connectionString, CreateDialect(connectionString, log)) { }

    public string ConnectionString { get; }

    public ISqlDialect Dialect { get; }

    public DbConnection Create() => Dialect.CreateConnection(ConnectionString);

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = Dialect.CreateConnection(ConnectionString);
        try
        {
            await cn.OpenAsync(ct);
            await Dialect.OnConnectionOpenedAsync(cn, ct);
            return cn;
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Picks the engine from the shape of the connection string.
    ///
    /// IN-PROGRESS: the schema layer (DispatchDbContext and the four migrations assemblies) supports
    /// PostgreSQL, SQLite, SQL Server and MySQL/MariaDB, but the repositories still run on Dapper through
    /// this factory and have only been ported to the first two. Until that port lands, resolving a
    /// SQL Server or MySQL connection string here throws with an explanation rather than constructing an
    /// Npgsql connection from it and failing later with "Couldn't set data source".
    /// </summary>
    public static ISqlDialect CreateDialect(string connectionString, ILogger? log = null)
    {
        var provider = DatabaseProviderResolver.Resolve(connectionString);
        return provider switch
        {
            DatabaseProvider.Sqlite => new SqliteDialect(log),
            DatabaseProvider.Postgres => new PostgresDialect(log),
            _ => throw new NotSupportedException(
                $"The {provider} backend is not usable yet: its schema and migrations exist, but the " +
                "repositories have not been ported off the PostgreSQL/SQLite-only data layer. " +
                "Use Sqlite or Postgres for now."),
        };
    }

    /// <summary>
    /// A SQLite connection string is identified by a file-source keyword with no server keywords alongside
    /// it. Npgsql also accepts "Data Source" as an alias for Host, so server keywords have to win.
    /// </summary>
    internal static bool IsSqlite(string connectionString)
    {
        var keys = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keys.Overlaps(new[] { "Host", "Server", "Port", "Username", "User ID", "Password", "Database" }))
            return false;

        return keys.Contains("Data Source") || keys.Contains("DataSource") || keys.Contains("Filename");
    }
}
