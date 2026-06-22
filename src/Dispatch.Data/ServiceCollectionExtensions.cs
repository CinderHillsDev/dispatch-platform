using Dispatch.Core.ApiKeys;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Logging;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Smtp;
using Dispatch.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the SQL connection factory, migration runner, and all SQL repositories.</summary>
    public static IServiceCollection AddDispatchData(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(new SqlConnectionFactory(connectionString));
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
        services.AddSingleton<IDatabaseHealth, SqlDatabaseHealth>();
        services.AddSingleton<ISmtpCredentialRepository, SqlSmtpCredentialRepository>();
        services.AddSingleton<ILoggingSettings, SqlLoggingSettings>();
        services.AddSingleton<IRetrySettings, SqlRetrySettings>();
        services.AddSingleton<IPurgeSettings, SqlPurgeSettings>();

        return services;
    }
}
