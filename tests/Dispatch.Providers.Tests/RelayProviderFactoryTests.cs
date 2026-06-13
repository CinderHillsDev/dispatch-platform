using Dispatch.Core.Providers;
using Dispatch.Core.Spool;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class RelayProviderFactoryTests
{
    private static RelayProviderFactory Factory()
    {
        var spool = new SpoolDirectory(Path.Combine(Path.GetTempPath(), "dispatch-factory-tests", Guid.NewGuid().ToString("N")));
        return new(new StubHttpClientFactory(), new SendGridClientFactory(), new EmailClientFactory(), spool);
    }

    [Theory]
    [InlineData(RelayProviderType.Local, typeof(LocalProvider))]
    [InlineData(RelayProviderType.Smtp, typeof(SmtpProvider))]
    [InlineData(RelayProviderType.Mailgun, typeof(MailgunProvider))]
    [InlineData(RelayProviderType.SendGrid, typeof(SendGridProvider))]
    [InlineData(RelayProviderType.AzureCommunication, typeof(AzureProvider))]
    public void Builds_expected_provider_type(RelayProviderType provider, Type expected)
    {
        var built = Factory().Build(new RelayConfig { Provider = provider });
        Assert.IsType(expected, built);
    }

    [Fact]
    public void Unconfigured_provider_refuses_to_relay()
    {
        // Out-of-the-box default: no provider chosen → permanent failure, never a silent discard.
        Assert.Throws<InvalidOperationException>(() =>
            Factory().Build(new RelayConfig { Provider = RelayProviderType.Unconfigured }));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
