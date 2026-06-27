using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dispatch.Core.Configuration;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;

namespace Dispatch.Service;

/// <summary>
/// Hosts the <see cref="SmtpServer.SmtpServer"/> for the process lifetime (spec §5, §19.2). Wires the
/// spool message store, the CIDR/size mailbox filter, and (optionally) SMTP AUTH against the configured
/// credential allow-list and STARTTLS from a PFX certificate.
/// </summary>
public sealed class SmtpListenerService : BackgroundService
{
    private readonly SpoolMessageStore _messageStore;
    private readonly CidrMailboxFilter _mailboxFilter;
    private readonly ConfiguredUserAuthenticator _authenticator;
    private readonly ConnectionTracker _connections;
    private readonly ListenerOptions _options;
    private readonly SmtpListenerState _state;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SmtpListenerService> _log;

    public SmtpListenerService(
        SpoolMessageStore messageStore,
        CidrMailboxFilter mailboxFilter,
        ConfiguredUserAuthenticator authenticator,
        ConnectionTracker connections,
        IOptions<ListenerOptions> options,
        SmtpListenerState state,
        IHostEnvironment env,
        ILogger<SmtpListenerService> log)
    {
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _authenticator = authenticator;
        _connections = connections;
        _options = options.Value;
        _state = state;
        _env = env;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ports = ResolveBindablePorts(_options.EffectivePorts);
        if (ports.Length == 0)
        {
            // Nothing bindable (already logged). Return without starting the SMTP server so the rest of the
            // host (dashboard + ingestion API) keeps running — a missing SMTP port must never take it down.
            _state.ListeningPorts = [];
            return;
        }
        // Publish the resolved set so /health and the dashboard can show what's actually listening (which may
        // differ from the configured ports when 25 fell back to 2525).
        _state.ListeningPorts = ports;
        var certificate = LoadCertificate();

        var optionsBuilder = new SmtpServerOptionsBuilder().ServerName(_options.ServerName);
        foreach (var port in ports)
        {
            optionsBuilder.Endpoint(e =>
            {
                e.Port(port, isSecure: false);
                // Secure default: AUTH is only offered after STARTTLS so credentials never cross the wire in
                // the clear. Operators can re-enable plaintext AUTH (internal/dev) via listener.allow_unsecure_auth.
                e.AllowUnsecureAuthentication(_options.AllowUnsecureAuth);
                if (_options.RequireAuth) e.AuthenticationRequired();
                if (certificate is not null)
                {
                    e.Certificate(certificate);
                    // Pin a modern TLS floor for STARTTLS (no TLS 1.0/1.1 fallback to the OS default).
                    e.SupportedSslProtocols(
                        System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
                }
            });
        }
        if (_options.MaxMessageBytes is > 0 and <= int.MaxValue)
            optionsBuilder.MaxMessageSize((int)_options.MaxMessageBytes);
        if (_options.ConnectionTimeoutSeconds > 0)
            optionsBuilder.CommandWaitTimeout(TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds));

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(_messageStore);
        serviceProvider.Add(_mailboxFilter);
        serviceProvider.Add(_authenticator);

        var server = new SmtpServer.SmtpServer(optionsBuilder.Build(), serviceProvider);

        // Track live connections for the max-connections cap (spec §5.3). The cap is enforced at MAIL FROM
        // by CidrMailboxFilter (the library accepts the TCP connection before these events fire).
        server.SessionCreated += (_, _) => _connections.Increment();
        server.SessionCompleted += (_, _) => _connections.Decrement();
        server.SessionFaulted += (_, _) => _connections.Decrement();
        server.SessionCancelled += (_, _) => _connections.Decrement();

        _log.LogInformation("SMTP listener starting on port(s) {Ports}{Auth}{Tls} (timeout {Timeout}s, max {Max} conns)",
            string.Join(", ", ports),
            _options.RequireAuth ? " (AUTH required)" : "",
            certificate is not null ? " (STARTTLS)" : "",
            _options.ConnectionTimeoutSeconds, _options.MaxConnections);

        try
        {
            await server.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            // A bind failure here (e.g. a port was taken in the race between our probe and the actual bind)
            // must NOT crash the host — by default an unhandled exception in a BackgroundService stops the
            // whole app. Log and return so the dashboard + ingestion API stay up.
            _state.ListeningPorts = [];
            _log.LogError(ex, "SMTP listener failed to start — the dashboard and ingestion API are unaffected");
            return;
        }

        _state.ListeningPorts = [];
        _log.LogInformation("SMTP listener stopped");
    }

    /// <summary>
    /// Resolve the ports we can actually bind. We prefer the configured ports (25 + 587 by default), but
    /// binding the privileged ports needs root / CAP_NET_BIND_SERVICE, and a port may already be taken by
    /// another MTA. Each port is probed; unbindable ones are dropped with a warning. If port 25 was requested
    /// but can't be bound (in use or no privilege), we fall back to the unprivileged 2525 so mail still flows.
    /// </summary>
    private int[] ResolveBindablePorts(int[] requested)
    {
        var result = new List<int>();
        var dropped = new List<int>();
        foreach (var p in requested)
            (CanBind(p) ? result : dropped).Add(p);

        foreach (var p in dropped)
            _log.LogWarning("SMTP port {Port} is unavailable (already in use or insufficient privilege) — skipping", p);

        // Fall back to 2525 only when port 25 was wanted but couldn't be bound, or nothing bound at all.
        if ((dropped.Contains(25) || result.Count == 0)
            && !result.Contains(ListenerOptions.FallbackPort)
            && CanBind(ListenerOptions.FallbackPort))
        {
            _log.LogWarning("Falling back to SMTP port {Fallback} (port 25 unavailable)", ListenerOptions.FallbackPort);
            result.Add(ListenerOptions.FallbackPort);
        }

        if (result.Count == 0)
            _log.LogError("No SMTP port could be bound from [{Requested}] — the listener will not accept mail",
                string.Join(", ", requested));

        return [.. result];
    }

    /// <summary>Probe whether a TCP port can be bound. EACCES (privilege) and EADDRINUSE (in use) both surface
    /// as <see cref="SocketException"/>, which is exactly what we treat as "unavailable".</summary>
    private static bool CanBind(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private X509Certificate2? LoadCertificate()
    {
        // Prefer the operator's shared TLS certificate (also secures the HTTPS API) when configured.
        if (!string.IsNullOrWhiteSpace(_options.TlsCertPath))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(_options.TlsCertPath, _options.TlsCertPassword);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Failed to load the shared TLS certificate from {Path}; falling back to a self-signed cert for STARTTLS",
                    _options.TlsCertPath);
            }
        }

        // No shared cert configured (or it failed to load): use the same auto-generated, persisted self-signed
        // cert the dashboard and HTTPS API use, so STARTTLS is available out of the box. Operators can replace
        // it with a CA-trusted shared cert in the dashboard (Settings -> TLS certificate).
        try
        {
            return SelfSignedCert.GetOrCreate(_env.ContentRootPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not obtain a self-signed certificate; STARTTLS disabled");
            return null;
        }
    }
}
