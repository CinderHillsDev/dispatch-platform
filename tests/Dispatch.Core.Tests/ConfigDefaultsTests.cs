using Dispatch.Core.Configuration;

namespace Dispatch.Core.Tests;

/// <summary>
/// Guards the seeded source-IP allow-list defaults (spec §1.1). A loopback-only default made the
/// dashboard unreachable on every real deployment (headless servers, NAT'd containers); these tests
/// lock in the deployment-friendly defaults so they can't silently regress.
/// </summary>
public class ConfigDefaultsTests
{
    private static string Default(string key) =>
        ConfigDefaults.Defaults.TryGetValue(key, out var v) ? v : throw new Xunit.Sdk.XunitException($"no default for {key}");

    [Fact]
    public void Dashboard_defaults_to_allow_all()
    {
        // The dashboard is password-protected and governed by its own middleware (empty = allow all),
        // so a headless/NAT'd server stays reachable for first login.
        Assert.Equal("[]", Default(ConfigKeys.WebUiAllowedCidrs));
    }

    [Theory]
    [InlineData(ConfigKeys.ListenerAllowedCidrs)]
    [InlineData(ConfigKeys.ApiAllowedCidrs)]
    public void Smtp_and_api_default_to_loopback_plus_private_ranges_only(string key)
    {
        // Closed model (spec §17.10): the SMTP listener and ingestion API only accept listed source IPs,
        // and an empty list denies everyone. The deployment-friendly default is loopback + private ranges.
        var cidrs = Default(key);

        // Headless/LAN/Docker submitters work...
        Assert.Contains("127.0.0.1/32", cidrs);
        Assert.Contains("10.0.0.0/8", cidrs);
        Assert.Contains("172.16.0.0/12", cidrs);
        Assert.Contains("192.168.0.0/16", cidrs);

        // ...but it is NOT an open internet relay, and NOT loopback-only (the old broken default).
        Assert.DoesNotContain("0.0.0.0/0", cidrs);
        Assert.DoesNotContain("::/0", cidrs);
        Assert.NotEqual("[\"127.0.0.1/32\",\"::1/128\"]", cidrs);
    }

    [Fact]
    public void Listener_is_not_open_relay_by_default()
    {
        // Defense in depth: even if the allow-list were widened, intake auth stays off by default,
        // so the private-range allow-list is what prevents an open relay out of the box.
        Assert.Equal("false", Default(ConfigKeys.ListenerRequireAuth));
    }

    [Fact]
    public void Api_defaults_to_plain_http_with_https_off_on_8026()
    {
        // Plain HTTP is on out of the box; HTTPS is opt-in (needs a TLS cert) on its own port.
        Assert.Equal("true", Default(ConfigKeys.ApiHttpEnabled));
        Assert.Equal("false", Default(ConfigKeys.ApiTlsEnabled));
        Assert.Equal("8026", Default(ConfigKeys.ApiTlsPort));
    }
}
