using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Smtp;
using Dispatch.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL connection factory, migration runner, and all SQL repositories. The database
    /// engine is inferred from the connection string (see <see cref="SqlConnectionFactory.CreateDialect"/>),
    /// so a Postgres deployment and a SQLite one differ only by that one setting.
    /// </summary>
    public static IServiceCollection AddDispatchData(
        this IServiceCollection services, string connectionString, string? providerName = null)
    {
        var provider = DatabaseProviderResolver.Resolve(connectionString, providerName);

        services.AddSingleton(sp => new DatabaseBootstrap(
            provider, connectionString, sp.GetService<ILogger<DatabaseBootstrap>>()));

        // A factory rather than a scoped DbContext: the repositories below are singletons called
        // concurrently from SpoolWorkerPool's worker threads, and a DbContext is not thread-safe.
        services.AddDbContextFactory<DispatchDbContext>(o =>
            DispatchDbContextFactory.Configure(o, provider, connectionString));

        services.AddSingleton(sp => new SqlConnectionFactory(
            connectionString,
            SqlConnectionFactory.CreateDialect(
                connectionString,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Dispatch.Data"))));
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<SqlLogRepository>();
        services.AddSingleton<ILogRepository>(sp => sp.GetRequiredService<SqlLogRepository>());

        services.AddSingleton<SqlCounterRepository>();
        services.AddSingleton<ICounterRepository>(sp => sp.GetRequiredService<SqlCounterRepository>());
        services.AddSingleton<ICounterReader>(sp => sp.GetRequiredService<SqlCounterRepository>());

        services.AddSingleton<Dispatch.Core.Audit.IAuditLog, SqlAuditLog>();

        services.AddSingleton<IConfigRepository, SqlConfigRepository>();
        services.AddSingleton<IRelaySettingsStore, SqlRelaySettingsStore>();
        services.AddSingleton<IRelayRepository, SqlRelayRepository>();
        services.AddSingleton<IRoutingRuleRepository, SqlRoutingRuleRepository>();
        services.AddSingleton<IApiKeyRepository, SqlApiKeyRepository>();
        services.AddSingleton<IMessageLogQuery, SqlMessageLogQuery>();
        services.AddSingleton<ILogMaintenance, SqlLogMaintenance>();
        services.AddSingleton<Dispatch.Core.Maintenance.IStorageReport, SqlStorageReport>();
        services.AddSingleton<IDatabaseHealth, SqlDatabaseHealth>();
        services.AddSingleton<ISmtpCredentialRepository, SqlSmtpCredentialRepository>();
        services.AddSingleton<ILoggingSettings, SqlLoggingSettings>();
        services.AddSingleton<IRetrySettings, SqlRetrySettings>();
        services.AddSingleton<IPurgeSettings, SqlPurgeSettings>();

        return services;
    }
}
