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
        RelayProviderType.AmazonSes => new AmazonSesProvider(config),
        RelayProviderType.Postmark => new PostmarkProvider(config, httpClientFactory.CreateClient("postmark")),
        RelayProviderType.Resend => new ResendProvider(config, httpClientFactory.CreateClient("resend")),
        RelayProviderType.SparkPost => new SparkPostProvider(config, httpClientFactory.CreateClient("sparkpost")),
        RelayProviderType.Smtp2Go => new Smtp2GoProvider(config, httpClientFactory.CreateClient("smtp2go")),
        RelayProviderType.Maileroo => new MailerooProvider(config, httpClientFactory.CreateClient("maileroo")),
        _ => throw new NotSupportedException($"Relay provider '{config.Provider}' is not supported."),
    };
}
