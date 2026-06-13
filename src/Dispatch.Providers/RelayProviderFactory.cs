using System.Net.Http;
using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>Builds an <see cref="IRelayProvider"/> from a <see cref="RelayConfig"/> (spec §8).</summary>
public sealed class RelayProviderFactory(
    IHttpClientFactory httpClientFactory,
    ISendGridClientFactory sendGridClientFactory,
    IEmailClientFactory emailClientFactory) : IRelayProviderFactory
{
    public IRelayProvider Build(RelayConfig config) => config.Provider switch
    {
        RelayProviderType.None => new NoneProvider(),
        RelayProviderType.Smtp => new SmtpProvider(config),
        RelayProviderType.Mailgun => new MailgunProvider(config, httpClientFactory.CreateClient("mailgun")),
        RelayProviderType.SendGrid => new SendGridProvider(config, sendGridClientFactory),
        RelayProviderType.AzureCommunication => new AzureProvider(config, emailClientFactory),
        _ => throw new NotSupportedException($"Relay provider '{config.Provider}' is not supported."),
    };
}
