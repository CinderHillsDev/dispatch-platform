using Dispatch.Data.Providers;

namespace Dispatch.Data;

/// <summary>The database engines Dispatch supports. See <see cref="DatabaseProviders"/> for the registry.</summary>
public enum DatabaseProvider
{
    /// <summary>Bundled and embedded - a file beside the service, no server to install. The default.</summary>
    Sqlite,
    Postgres,
    SqlServer,
    /// <summary>MariaDB or MySQL, via Pomelo.</summary>
    MySql,
}

/// <summary>
/// Resolves which engine a connection string targets. Thin wrapper over
/// <see cref="DatabaseProviders.Resolve"/>, kept because callers want the enum rather than the provider.
/// </summary>
public static class DatabaseProviderResolver
{
    public static DatabaseProvider Resolve(string connectionString, string? explicitProvider = null) =>
        DatabaseProviders.Resolve(connectionString, explicitProvider).Id;
}
