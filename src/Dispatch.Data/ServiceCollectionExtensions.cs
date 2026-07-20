using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Smtp;
using Dispatch.Data.Providers;
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

        // A POOLED factory, not a scoped DbContext. The repositories below are singletons called
        // concurrently from SpoolWorkerPool's worker threads, and a DbContext is not thread-safe, so each
        // operation takes its own context. On the ingest path that is several contexts per message from
        // many threads at once; pooling resets and reuses instances instead of re-allocating the change
        // tracker and internal scope every time. Measured, this is the difference between roughly 1,000 and
        // several thousand concurrent writes per second on SQLite.
        //
        // This must stay in sync with DispatchDbContextFactory.Create, which the migrator and tests use and
        // which is also pooled - otherwise a benchmark run through the test path would report a throughput
        // production never sees. That is precisely the gap this once had: DI registered the non-pooled
        // AddDbContextFactory while the tests ran pooled.
        services.AddPooledDbContextFactory<DispatchDbContext>(o =>
            DispatchDbContextFactory.Configure(o, provider, connectionString));

        // The repositories reach the engine only through this, for the few things EF cannot express.
        services.AddSingleton(DatabaseProviders.Get(provider));

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
