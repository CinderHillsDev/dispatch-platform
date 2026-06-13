using Dispatch.Core.Logging;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dispatch.Web;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the web/ingestion services: SignalR, the live-event stream, rate limiter, API-key
    /// middleware, and the ingestion handler. Also wraps the registered <see cref="ILogRepository"/> in
    /// <see cref="BroadcastingLogRepository"/> so persisted events drive the live feed. Call AFTER the
    /// data layer is registered (so an inner <see cref="ILogRepository"/> exists to decorate).
    /// </summary>
    public static IServiceCollection AddDispatchWeb(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<RelayEventStream>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<ApiMessageHandler>();
        services.AddScoped<ApiKeyMiddleware>();

        // Decorate the existing ILogRepository registration with the broadcaster.
        var inner = services.Single(d => d.ServiceType == typeof(ILogRepository));
        services.Remove(inner);
        services.AddSingleton<ILogRepository>(sp =>
        {
            var innerImpl = (ILogRepository)CreateInstance(sp, inner);
            return new BroadcastingLogRepository(innerImpl, sp.GetRequiredService<RelayEventStream>());
        });

        return services;
    }

    private static object CreateInstance(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null) return descriptor.ImplementationInstance;
        if (descriptor.ImplementationFactory is not null) return descriptor.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }
}
