namespace Dispatch.Data.Providers;

/// <summary>
/// The registry of supported engines, and the rules for choosing one.
///
/// ADDING AN ENGINE: implement <see cref="IDatabaseProvider"/>, create a matching migrations assembly, and
/// add one line to <see cref="All"/>. Nothing else in the codebase enumerates engines - the DbContext, the
/// initializer, the migrator and the test matrix all read this list - so that one line is what makes the
/// shared test suite start running against it.
/// </summary>
public static class DatabaseProviders
{
    /// <summary>Every supported engine. Order is not significant.</summary>
    public static IReadOnlyList<IDatabaseProvider> All { get; } =
    [
        new SqliteDatabaseProvider(),
        new PostgresDatabaseProvider(),
        new SqlServerDatabaseProvider(),
        new MySqlDatabaseProvider(),
    ];

    public static IDatabaseProvider Get(DatabaseProvider id) =>
        All.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"No database provider is registered for '{id}'.");

    /// <summary>
    /// Resolves the engine from an explicit setting (the <c>Database:Provider</c> configuration key) if
    /// given, otherwise from the shape of the connection string.
    ///
    /// Sniffing is a convenience for the unambiguous cases and is deliberately not trusted beyond them:
    /// "Server=host;Database=db;User Id=sa" is valid for BOTH SQL Server and MySQL, and guessing wrong
    /// would not fail cleanly - it would fail later at some query with a confusing syntax error. So an
    /// ambiguous string throws and asks for the setting rather than picking.
    /// </summary>
    public static IDatabaseProvider Resolve(string connectionString, string? explicitProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProvider))
        {
            var wanted = explicitProvider.Trim();
            return All.FirstOrDefault(p => p.Aliases.Contains(wanted))
                ?? throw new InvalidOperationException(
                    $"Database:Provider '{explicitProvider}' is not recognised. Use one of: " +
                    string.Join(", ", All.Select(p => p.Id)) + ".");
        }

        var keys = Keywords(connectionString);

        // A distinctive keyword is by definition claimed by exactly one engine.
        var byKeyword = All.Where(p => p.DistinctiveKeywords.Overlaps(keys)).ToList();
        if (byKeyword.Count == 1) return byKeyword[0];
        if (byKeyword.Count > 1)
            throw new InvalidOperationException(
                "The connection string matches more than one database engine (" +
                string.Join(", ", byKeyword.Select(p => p.DisplayName)) +
                "). Set Database:Provider explicitly.");

        // SQLite is the only engine without a server, so a file source and no server keyword is decisive.
        var hasFileSource = keys.Contains("data source") || keys.Contains("datasource");
        var hasServerish = keys.Overlaps(ServerKeywords);
        if (hasFileSource && !hasServerish)
            return Get(DatabaseProvider.Sqlite);

        if (hasServerish || hasFileSource)
            throw new InvalidOperationException(
                "The database engine cannot be determined from this connection string - the keywords it " +
                "uses are shared by more than one engine (SQL Server and MySQL/MariaDB both accept " +
                "Server/Database/User Id). Set Database:Provider to one of: " +
                string.Join(", ", All.Select(p => p.Id)) + ".");

        throw new InvalidOperationException(
            "The connection string does not look like any supported database. Set Database:Provider to one " +
            "of: " + string.Join(", ", All.Select(p => p.Id)) + ".");
    }

    /// <summary>Keywords that imply a network server, and therefore rule SQLite out.</summary>
    private static readonly HashSet<string> ServerKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "server", "port", "user id", "uid", "username", "password", "pwd", "initial catalog", "database",
    };

    private static HashSet<string> Keywords(string connectionString) =>
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
