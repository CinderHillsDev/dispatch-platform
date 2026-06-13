using Dispatch.Core.Configuration;
using Dispatch.Core.Spool;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;

namespace Dispatch.Service;

/// <summary>
/// Hosts the <see cref="SmtpServer.SmtpServer"/> for the process lifetime (spec §5, §19.2).
/// Wires the spool message store and the CIDR/size mailbox filter into SmtpServer's container.
/// </summary>
public sealed class SmtpListenerService : BackgroundService
{
    private readonly SpoolMessageStore _messageStore;
    private readonly CidrMailboxFilter _mailboxFilter;
    private readonly ListenerOptions _options;
    private readonly ILogger<SmtpListenerService> _log;

    public SmtpListenerService(
        SpoolMessageStore messageStore,
        CidrMailboxFilter mailboxFilter,
        IOptions<ListenerOptions> options,
        ILogger<SmtpListenerService> log)
    {
        _messageStore = messageStore;
        _mailboxFilter = mailboxFilter;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ports = _options.EffectivePorts;
        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName(_options.ServerName)
            .Port(ports);

        if (_options.MaxMessageBytes is > 0 and <= int.MaxValue)
            optionsBuilder.MaxMessageSize((int)_options.MaxMessageBytes);

        var serverOptions = optionsBuilder.Build();

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(_messageStore);
        serviceProvider.Add(_mailboxFilter);

        var server = new SmtpServer.SmtpServer(serverOptions, serviceProvider);

        _log.LogInformation("SMTP listener starting on port(s) {Ports}",
            string.Join(", ", ports));

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
}
