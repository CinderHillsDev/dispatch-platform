using Dispatch.Core.Configuration;

namespace Dispatch.Core.Tests;

/// <summary>
/// The ingestion API can be bound on a plain-HTTP port and/or an HTTPS port; <see cref="ApiOptions.IsApiPort"/>
/// decides whether an incoming connection's local port belongs to the API (gates endpoints + auth middleware).
/// </summary>
public class ApiOptionsTests
{
    private static ApiOptions Make(bool http, bool tls) =>
        new() { Port = 8025, HttpEnabled = http, TlsEnabled = tls, TlsPort = 8026 };

    [Fact]
    public void Http_only_matches_http_port_not_tls_port()
    {
        var o = Make(http: true, tls: false);
        Assert.True(o.IsApiPort(8025));
        Assert.False(o.IsApiPort(8026));
        Assert.False(o.IsApiPort(8420));
    }

    [Fact]
    public void Tls_only_matches_tls_port_not_http_port()
    {
        var o = Make(http: false, tls: true);
        Assert.False(o.IsApiPort(8025));
        Assert.True(o.IsApiPort(8026));
    }

    [Fact]
    public void Both_enabled_matches_either_port()
    {
        var o = Make(http: true, tls: true);
        Assert.True(o.IsApiPort(8025));
        Assert.True(o.IsApiPort(8026));
        Assert.False(o.IsApiPort(9999));
    }

    [Fact]
    public void Both_disabled_matches_nothing()
    {
        var o = Make(http: false, tls: false);
        Assert.False(o.IsApiPort(8025));
        Assert.False(o.IsApiPort(8026));
    }
}
