using System.Net.Sockets;
using Dispatch.Providers;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Dispatch.Providers.Tests;

// SmtpProvider needs a live SMTP server to exercise SendAsync, but its two pure decision functions —
// TLS-mode parsing and transient/permanent error classification — are unit-testable and security/retry relevant.
public class SmtpProviderTests
{
    [Theory]
    [InlineData("none", SecureSocketOptions.None)]
    [InlineData("None", SecureSocketOptions.None)]
    [InlineData("starttls", SecureSocketOptions.StartTls)]
    [InlineData("ssl", SecureSocketOptions.SslOnConnect)]
    [InlineData("sslonconnect", SecureSocketOptions.SslOnConnect)]
    [InlineData("starttlswhenavailable", SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData("auto", SecureSocketOptions.Auto)]
    [InlineData("", SecureSocketOptions.Auto)]
    [InlineData(null, SecureSocketOptions.Auto)]
    [InlineData("garbage", SecureSocketOptions.Auto)]   // unknown → Auto, never throws
    public void ParseTls_maps_modes(string? mode, SecureSocketOptions expected) =>
        Assert.Equal(expected, SmtpProvider.ParseTls(mode));

    [Fact]
    public void IsTransient_true_for_4xx_protocol_socket_io_timeout()
    {
        Assert.True(SmtpProvider.IsTransient(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, (SmtpStatusCode)451, "busy")));
        Assert.True(SmtpProvider.IsTransient(new SmtpProtocolException("proto")));
        Assert.True(SmtpProvider.IsTransient(new SocketException()));
        Assert.True(SmtpProvider.IsTransient(new IOException("io")));
        Assert.True(SmtpProvider.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void IsTransient_false_for_5xx_and_unrelated()
    {
        Assert.False(SmtpProvider.IsTransient(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, (SmtpStatusCode)550, "rejected")));
        Assert.False(SmtpProvider.IsTransient(new InvalidOperationException("config")));
    }
}
