using Dispatch.Core.Providers;
using Dispatch.Core.Relays;

namespace Dispatch.Core.Tests;

public class SmtpPortGuardTests
{
    [Theory]
    [InlineData("25", true)]
    [InlineData("", true)]        // blank -> SmtpProvider defaults to port 25
    [InlineData(null, true)]
    [InlineData("587", false)]
    [InlineData("2525", false)]
    [InlineData("465", false)]
    public void Smtp_provider_effective_port_25_is_detected(string? port, bool expected)
    {
        var settings = new Dictionary<string, string?> { ["Host"] = "smtp.example.com", ["Port"] = port };
        Assert.Equal(expected, SmtpPortGuard.UsesOutboundPort25(RelayProviderType.Smtp, settings));
    }

    [Fact]
    public void Missing_port_key_is_treated_as_25()
    {
        var settings = new Dictionary<string, string?> { ["Host"] = "smtp.example.com" };
        Assert.True(SmtpPortGuard.UsesOutboundPort25(RelayProviderType.Smtp, settings));
    }

    [Theory]
    [InlineData(RelayProviderType.Local)]
    [InlineData(RelayProviderType.SendGrid)]
    [InlineData(RelayProviderType.Mailgun)]
    public void Non_smtp_providers_are_never_flagged(RelayProviderType provider)
    {
        // Even with a literal Port=25 setting, only the generic SMTP provider makes an outbound port-25 connection.
        var settings = new Dictionary<string, string?> { ["Port"] = "25" };
        Assert.False(SmtpPortGuard.UsesOutboundPort25(provider, settings));
    }
}
