namespace Dispatch.Data;

/// <summary>The database engines Dispatch supports.</summary>
public enum DatabaseProvider
{
    /// <summary>Bundled, embedded, no server to install. The default deployment shape.</summary>
    Sqlite,
    Postgres,
    SqlServer,
    /// <summary>MariaDB or MySQL, via Pomelo.</summary>
    MySql,
}

/// <summary>
/// Resolves which engine a connection string targets.
///
/// Sniffing alone is not sufficient and deliberately not trusted here: "Server=host;Database=db;User
/// Id=sa" is a valid connection string for BOTH SQL Server and MySQL, and guessing wrong would not fail
/// cleanly — it would fail at some later query with a confusing syntax error. So an explicit setting always
/// wins, sniffing is a convenience for the unambiguous cases, and anything genuinely ambiguous throws with
/// instructions rather than picking.
/// </summary>
public static class DatabaseProviderResolver
{
    /// <summary>
    /// Resolves the provider from an optional explicit setting (the <c>Database:Provider</c> configuration
    /// key) falling back to connection-string shape.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The explicit value is not a known engine, or the connection string is ambiguous or unrecognised.
    /// </exception>
    public static DatabaseProvider Resolve(string connectionString, string? explicitProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProvider))
        {
            // Accept the common spellings operators actually write.
            return explicitProvider.Trim().ToLowerInvariant() switch
            {
                "sqlite" => DatabaseProvider.Sqlite,
                "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
                "sqlserver" or "mssql" or "sql server" => DatabaseProvider.SqlServer,
                "mysql" or "mariadb" => DatabaseProvider.MySql,
                _ => throw new InvalidOperationException(
                    $"Database:Provider '{explicitProvider}' is not recognised. " +
                    "Use one of: Sqlite, Postgres, SqlServer, MySql (MariaDB is MySql)."),
            };
        }

        var keys = Keywords(connectionString);

        // SQLite is the only engine with no server, so a file source and no server keyword is unambiguous.
        var hasServer = keys.Overlaps(["host", "server", "port", "data source", "addr", "address", "network address"]);
        var hasFileSource = keys.Contains("data source") || keys.Contains("datasource") || keys.Contains("filename");
        if (hasFileSource && !keys.Overlaps(["host", "server", "port", "user id", "uid", "username", "password", "initial catalog"]))
            return DatabaseProvider.Sqlite;

        // "Host=" is Npgsql's spelling and neither SqlClient nor MySqlConnector uses it as the primary key.
        if (keys.Contains("host"))
            return DatabaseProvider.Postgres;

        // Keywords unique to SqlClient.
        if (keys.Overlaps(["initial catalog", "trusted_connection", "integrated security", "trustservercertificate", "encrypt", "application intent", "multisubnetfailover"]))
            return DatabaseProvider.SqlServer;

        // Keywords unique to MySqlConnector.
        if (keys.Overlaps(["uid", "sslmode", "allowuservariables", "allowpublickeyretrieval", "treattinyasboolean", "server version"]))
            return DatabaseProvider.MySql;

        // "Server=...;Database=...;User Id=...;Password=..." is valid for both SQL Server and MySQL.
        // Refuse rather than coin-flip: a wrong guess surfaces much later as an opaque SQL syntax error.
        if (hasServer)
            throw new InvalidOperationException(
                "The connection string could target either SQL Server or MySQL/MariaDB and cannot be " +
                "resolved unambiguously. Set Database:Provider explicitly to SqlServer or MySql " +
                "(or Database__Provider as an environment variable).");

        throw new InvalidOperationException(
            "Could not determine the database engine from the connection string. " +
            "Set Database:Provider to one of: Sqlite, Postgres, SqlServer, MySql.");
    }

    private static HashSet<string> Keywords(string connectionString) =>
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0].Trim().ToLowerInvariant())
            .ToHashSet();
}
