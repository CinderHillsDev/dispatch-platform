using System.Security.Cryptography.X509Certificates;
using Dispatch.Core.Configuration;
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
    private readonly ListenerOptions _options;
    private readonly ILogger<SmtpListenerService> _log;

    public SmtpListenerService(
        SpoolMessageStore messageStore,
        CidrMailboxFilter mailboxFilter,
        ConfiguredUserAuthenticator authenticator,
        IOptions<ListenerOptions> options,
        ILogger<SmtpListenerService> log)
    {
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _authenticator = authenticator;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ports = _options.EffectivePorts;
        var certificate = LoadCertificate();

        var optionsBuilder = new SmtpServerOptionsBuilder().ServerName(_options.ServerName);
        foreach (var port in ports)
        {
            optionsBuilder.Endpoint(e =>
            {
                e.Port(port, isSecure: false);
                e.AllowUnsecureAuthentication(true);   // permit AUTH without TLS (internal/dev use)
                if (_options.RequireAuth) e.AuthenticationRequired();
                if (certificate is not null) e.Certificate(certificate);
            });
        }
        if (_options.MaxMessageBytes is > 0 and <= int.MaxValue)
            optionsBuilder.MaxMessageSize((int)_options.MaxMessageBytes);

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(_messageStore);
        serviceProvider.Add(_mailboxFilter);
        serviceProvider.Add(_authenticator);

        var server = new SmtpServer.SmtpServer(optionsBuilder.Build(), serviceProvider);

        _log.LogInformation("SMTP listener starting on port(s) {Ports}{Auth}{Tls}",
            string.Join(", ", ports),
            _options.RequireAuth ? " (AUTH required)" : "",
            certificate is not null ? " (STARTTLS)" : "");

        try
        {
            await server.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        _log.LogInformation("SMTP listener stopped");
    }

    private X509Certificate2? LoadCertificate()
    {
        if (string.IsNullOrWhiteSpace(_options.TlsCertPath)) return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(_options.TlsCertPath, _options.TlsCertPassword);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load TLS certificate from {Path}; STARTTLS disabled", _options.TlsCertPath);
            return null;
        }
    }
}
