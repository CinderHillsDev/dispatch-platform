using System.Net.Http;
using Dispatch.Core.Providers;
using Dispatch.Core.Spool;

namespace Dispatch.Providers;

/// <summary>Builds an <see cref="IRelayProvider"/> from a <see cref="RelayConfig"/> (spec §8).</summary>
public sealed class RelayProviderFactory(
    IHttpClientFactory httpClientFactory,
    ISendGridClientFactory sendGridClientFactory,
    IEmailClientFactory emailClientFactory,
    SpoolDirectory spool) : IRelayProviderFactory
{
    public IRelayProvider Build(RelayConfig config) => config.Provider switch
    {
        RelayProviderType.Unconfigured => throw new InvalidOperationException(
            "No relay provider is configured. Choose a provider under Relays before sending mail."),
        RelayProviderType.Local => new LocalProvider(spool.CapturedDir),
        RelayProviderType.Smtp => new SmtpProvider(config),
        RelayProviderType.Mailgun => new MailgunProvider(config, httpClientFactory.CreateClient("mailgun")),
        RelayProviderType.SendGrid => new SendGridProvider(config, sendGridClientFactory),
        RelayProviderType.AzureCommunication => new AzureProvider(config, emailClientFactory),
        _ => throw new NotSupportedException($"Relay provider '{config.Provider}' is not supported."),
    };
}
