using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;
using Dispatch.Data;
using Dispatch.Data.Repositories;
using Dispatch.Providers;
using Dispatch.Service;
using Dispatch.Web;
using Dispatch.Web.Endpoints;
using Dispatch.Web.Ingestion;
using Dispatch.Web.Realtime;
using Microsoft.Extensions.Options;
using Serilog;

var logDirectory = Environment.GetEnvironmentVariable("DISPATCH_LOG_DIR") ?? "logs";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDirectory, "dispatch-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31)   // spec §13: rolling daily with retention
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    // Integrate with the host service manager so it reports "running" correctly: Windows SCM (otherwise the
    // MSI's service start times out → 1603) and systemd Type=notify. Both are no-ops when run interactively.
    builder.Host.UseWindowsService();
    builder.Host.UseSystemd();

    // --- Bootstrap (spec §12.1, §12.6, §12.8) -------------------------------------------------
    // appsettings.json holds ONLY the DB connection string and the Web UI TLS cert. Everything else lives
    // in the SQL config table. SQL must be reachable at startup: initialise the schema, seed default config
    // on first run, and load the ConfigCache — the single source of truth — before configuring listeners.
    var connectionString = builder.Configuration.GetConnectionString("DispatchLog")
        ?? throw new InvalidOperationException("ConnectionStrings:DispatchLog is not configured.");

    var bootstrapFactory = new SqlConnectionFactory(connectionString);
    var bootstrapRepo = new SqlConfigRepository(bootstrapFactory);
    var configCache = new ConfigCache();
    try
    {
        await new DatabaseInitializer(bootstrapFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await ConfigDefaults.SeedAsync(bootstrapRepo);
        await configCache.LoadAsync(bootstrapRepo);
    }
    catch (Exception ex)
    {
        // §12.8: without config there is nothing safe to do — log clearly and exit non-zero so the
        // service manager retries on its configured restart interval.
        Log.Fatal(ex, "Dispatch cannot start: the SQL configuration database is unreachable.");
        return 1;
    }

    var listenerSnapshot = configCache.Listener();
    var apiSnapshot = configCache.Api();
    var webSnapshot = configCache.WebUi();
    var spoolSnapshot = configCache.Spool();
    // The Web UI TLS cert is the one setting that stays in appsettings (spec §12.1/§12.2).
    builder.Configuration.GetSection(WebUiOptions.SectionName).Bind(webSnapshot);

    // Kestrel: dashboard/read API on the web port, ingestion API on the API port (from SQL config).
    // The dashboard is HTTPS-only (spec §17.2): it uses the configured TLS cert (appsettings, §12.2) when
    // present, otherwise an auto-generated, persisted self-signed cert — never plain HTTP. The ingestion
    // API stays plain HTTP so devices/apps that can't do TLS can still post (it's gated by API keys).
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(webSnapshot.Port, lo =>
        {
            if (!string.IsNullOrWhiteSpace(webSnapshot.TlsCertPath))
                lo.UseHttps(webSnapshot.TlsCertPath,
                    string.IsNullOrEmpty(webSnapshot.TlsCertPassword) ? null : webSnapshot.TlsCertPassword);
            else
                lo.UseHttps(SelfSignedCert.GetOrCreate(builder.Environment.ContentRootPath));
        });
        k.ListenAnyIP(apiSnapshot.Port);
    });

    // ConfigCache is the runtime source of truth (spec §12.5). Section snapshots are exposed as IOptions for
    // the startup-bound consumers (listener ports, spool dir/workers) that only apply at (re)start anyway;
    // live consumers (size/CIDR/rate-limit filters) read ConfigCache directly.
    builder.Services.AddSingleton(configCache);
    builder.Services.AddSingleton(Options.Create(listenerSnapshot));
    builder.Services.AddSingleton(Options.Create(apiSnapshot));
    builder.Services.AddSingleton(Options.Create(webSnapshot));
    builder.Services.AddSingleton(Options.Create(spoolSnapshot));

    // Retry/purge/default-relay defaults come from the typed Options classes; the SQL-backed settings
    // providers (SqlRetrySettings/SqlPurgeSettings) read live values from the config table on top of them.
    builder.Services.Configure<RetryOptions>(_ => { });
    builder.Services.Configure<PurgeOptions>(_ => { });
    builder.Services.Configure<DefaultRelayOptions>(_ => { });

    // Core singletons. A relative spool directory is resolved against the content root (not the process
    // CWD, which is system32 for a Windows service / a read-only dir for the systemd unit), so the default
    // "./.dispatch-spool" lands beside the app data, not wherever the service happened to start.
    builder.Services.AddSingleton(sp =>
    {
        var dir = sp.GetRequiredService<IOptions<SpoolOptions>>().Value.Directory;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(builder.Environment.ContentRootPath, dir);
        return new SpoolDirectory(dir);
    });
    builder.Services.AddSingleton<IRelayResolver, RoutingEngine>();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<ISendGridClientFactory, SendGridClientFactory>();
    builder.Services.AddSingleton<IEmailClientFactory, EmailClientFactory>();
    builder.Services.AddSingleton<IRelayProviderFactory, RelayProviderFactory>();
    builder.Services.AddSingleton<MinuteCounterRing>();
    builder.Services.AddSingleton<RelayConcurrencyTracker>();
    builder.Services.AddSingleton<SpoolMessageStore>();
    builder.Services.AddSingleton<IntakeState>();
    builder.Services.AddSingleton<DiskMonitor>();
    builder.Services.AddSingleton<ConnectionTracker>();
    builder.Services.AddSingleton<CidrMailboxFilter>();
    builder.Services.AddSingleton<SmtpAuthThrottle>();
    builder.Services.AddSingleton<ConfiguredUserAuthenticator>();

    // SQL persistence (relay_log, relay_counters, relays, config, api_keys, message-log queries).
    builder.Services.AddDispatchData(connectionString);

    // Web/ingestion services (SignalR, live feed, rate limiter, API-key middleware) — must follow AddDispatchData.
    builder.Services.AddDispatchWeb(webSnapshot.RequireHttps,
        configCache.GetInt(ConfigKeys.WebUiSessionTimeoutMinutes, 480));

    // Hosted services: relay worker pool + SMTP listener.
    builder.Services.AddHostedService<SpoolWorkerPool>();
    builder.Services.AddHostedService<SmtpListenerService>();
    // PurgeWorker is a singleton (so the manual /api/purge/run endpoint can invoke it) AND a hosted service.
    builder.Services.AddSingleton<PurgeHistory>();
    builder.Services.AddSingleton<PurgeWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PurgeWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DiskMonitor>());

    var app = builder.Build();

    // Schema + default config were applied during bootstrap (before listeners were configured). The default
    // relay is seeded "Unconfigured" by the schema migration; an administrator selects its provider in the UI.
    var configRepo = app.Services.GetRequiredService<IConfigRepository>();

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

    app.UseSecurityHeaders(webSnapshot.Port, webSnapshot.RequireHttps);
    app.UseEmbeddedUi(webSnapshot.Port);
    app.UseAuthentication();
    app.UseMiddleware<Dispatch.Web.Auth.WebAuthMiddleware>();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.MapIngestionApi(apiSnapshot.Port);
    app.MapDashboardApi(webSnapshot.Port);
    app.MapPurgeOps(webSnapshot.Port);
    app.MapHub<LogHub>("/hub/logs");
    app.MapHub<TestProviderHub>("/hub/test-provider");
    app.MapEmbeddedUiFallback(webSnapshot.Port);

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
