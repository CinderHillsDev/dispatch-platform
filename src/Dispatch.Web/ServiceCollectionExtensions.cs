using Dispatch.Core.Logging;
using Dispatch.Web.Auth;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
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
    public static IServiceCollection AddDispatchWeb(this IServiceCollection services, bool secureCookies = false)
    {
        services.AddSignalR();
        services.AddSingleton<RelayEventStream>();
        services.AddSingleton<ProviderTestService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<Auth.LoginThrottle>();
        services.AddSingleton<ApiMessageHandler>();
        services.AddScoped<ApiKeyMiddleware>();
        services.AddScoped<WebAuthMiddleware>();

        // Optional web-UI cookie auth (enforced by WebAuthMiddleware only when configured).
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "dispatch.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                // Secure when the dashboard is fronted by TLS (WebUi:RequireHttps); SameAsRequest keeps the
                // cookie working over plain HTTP in local dev. Set RequireHttps=true in production.
                o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });

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
