using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class RelayProviderFactoryTests
{
    private static RelayProviderFactory Factory() =>
        new(new StubHttpClientFactory(), new SendGridClientFactory(), new EmailClientFactory());

    [Theory]
    [InlineData(RelayProviderType.None, typeof(NoneProvider))]
    [InlineData(RelayProviderType.Smtp, typeof(SmtpProvider))]
    [InlineData(RelayProviderType.Mailgun, typeof(MailgunProvider))]
    [InlineData(RelayProviderType.SendGrid, typeof(SendGridProvider))]
    [InlineData(RelayProviderType.AzureCommunication, typeof(AzureProvider))]
    public void Builds_expected_provider_type(RelayProviderType provider, Type expected)
    {
        var built = Factory().Build(new RelayConfig { Provider = provider });
        Assert.IsType(expected, built);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
