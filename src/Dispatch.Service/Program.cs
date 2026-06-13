using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;
using Dispatch.Data;
using Dispatch.Providers;
using Dispatch.Service;
using Dispatch.Web;
using Dispatch.Web.Endpoints;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dispatch-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
    var webOptions = builder.Configuration.GetSection(WebUiOptions.SectionName).Get<WebUiOptions>() ?? new WebUiOptions();

    // Kestrel: dashboard/read API on the web port, ingestion API on the API port.
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(webOptions.Port);
        k.ListenAnyIP(apiOptions.Port);
    });

    // Configuration sections (stand-in for the SQL config table for non-secret settings).
    builder.Services.Configure<SpoolOptions>(builder.Configuration.GetSection(SpoolOptions.SectionName));
    builder.Services.Configure<ListenerOptions>(builder.Configuration.GetSection(ListenerOptions.SectionName));
    builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection(RetryOptions.SectionName));
    builder.Services.Configure<DefaultRelayOptions>(builder.Configuration.GetSection(DefaultRelayOptions.SectionName));
    builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));
    builder.Services.Configure<WebUiOptions>(builder.Configuration.GetSection(WebUiOptions.SectionName));
    builder.Services.Configure<PurgeOptions>(builder.Configuration.GetSection(PurgeOptions.SectionName));

    // Core singletons.
    builder.Services.AddSingleton(sp =>
        new SpoolDirectory(sp.GetRequiredService<IOptions<SpoolOptions>>().Value.Directory));
    builder.Services.AddSingleton<IRelayResolver, RoutingEngine>();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<ISendGridClientFactory, SendGridClientFactory>();
    builder.Services.AddSingleton<IEmailClientFactory, EmailClientFactory>();
    builder.Services.AddSingleton<IRelayProviderFactory, RelayProviderFactory>();
    builder.Services.AddSingleton<MinuteCounterRing>();
    builder.Services.AddSingleton<SpoolMessageStore>();
    builder.Services.AddSingleton<CidrMailboxFilter>();

    // SQL persistence (relay_log, relay_counters, relays, config, api_keys, message-log queries).
    var connectionString = builder.Configuration.GetConnectionString("DispatchLog")
        ?? throw new InvalidOperationException("ConnectionStrings:DispatchLog is not configured.");
    builder.Services.AddDispatchData(connectionString);

    // Web/ingestion services (SignalR, live feed, rate limiter, API-key middleware) — must follow AddDispatchData.
    builder.Services.AddDispatchWeb();

    // Hosted services: relay worker pool + SMTP listener.
    builder.Services.AddHostedService<SpoolWorkerPool>();
    builder.Services.AddHostedService<SmtpListenerService>();
    builder.Services.AddHostedService<PurgeWorker>();

    var app = builder.Build();

    // Apply migrations before serving traffic.
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

    // Seed the default relay's provider/credentials from appsettings on first run; afterwards SQL is authoritative.
    var configRepo = app.Services.GetRequiredService<IConfigRepository>();
    if (await configRepo.GetAsync("relay:1:provider") is null)
    {
        var def = app.Services.GetRequiredService<IOptions<DefaultRelayOptions>>().Value;
        if (def.Provider != RelayProviderType.Unconfigured)
        {
            var relayRepo = app.Services.GetRequiredService<IRelayRepository>();
            var defaultRelay = await relayRepo.GetDefaultAsync();
            if (defaultRelay is not null)
                await relayRepo.UpdateAsync(defaultRelay.Id, defaultRelay.Name, def.Provider, enabled: true,
                    defaultRelay.MaxConcurrency, defaultRelay.MaxMessageBytes);
            await app.Services.GetRequiredService<IRelaySettingsStore>().SaveAsync(defaultRelay?.Id ?? 1,
                new RelaySettings(def.Provider, def.Settings.ToDictionary(k => k.Key, v => (string?)v.Value)));
        }
    }

    // Seed the admin password from install config on first run (the installer supplies AdminPassword).
    // If none is supplied, the web UI presents a one-time first-run setup screen instead.
    var adminPassword = builder.Configuration["AdminPassword"];
    if (!string.IsNullOrWhiteSpace(adminPassword)
        && string.IsNullOrEmpty(await configRepo.GetAsync(Dispatch.Web.Auth.AuthEndpoints.PasswordHashKey)))
    {
        await configRepo.SetAsync(Dispatch.Web.Auth.AuthEndpoints.PasswordHashKey,
            BCrypt.Net.BCrypt.HashPassword(adminPassword, 12), encrypted: false);
        Log.Information("Seeded admin password from install configuration");
    }

    app.UseEmbeddedUi(webOptions.Port);
    app.UseAuthentication();
    app.UseMiddleware<Dispatch.Web.Auth.WebAuthMiddleware>();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.MapIngestionApi(apiOptions.Port);
    app.MapDashboardApi(webOptions.Port);
    app.MapHub<LogHub>("/hub/logs");
    app.MapEmbeddedUiFallback(webOptions.Port);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Dispatch service terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
