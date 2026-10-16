using Dispatch.Core.Providers;
using MailKit.Net.Smtp;
using MailKit.Security;
using System.Net.Sockets;

namespace Dispatch.Providers;

/// <summary>
/// Generic SMTP upstream relay via MailKit (spec §8.4) - for AWS SES SMTP, Office 365, Postfix,
/// or any smart host. Settings: Host, Port, Username, Password, TlsMode (None|Auto|StartTls|SslOnConnect).
/// 4xx / connection errors are mapped to <see cref="TransientRelayException"/> so they are retried.
/// </summary>
public sealed class SmtpProvider : IRelayProvider
{
    private readonly RelayConfig _config;

    public SmtpProvider(RelayConfig config) => _config = config;

    public string Name => "Smtp";

    public Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        var host = Setting("Host");
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("SMTP relay 'Host' is not configured.");

        var port = int.TryParse(Setting("Port"), out var p) ? p : 25;
        var secure = ParseTls(Setting("TlsMode"));
        return SmtpDelivery.SendAsync(host, port, secure, Setting("Username"), Setting("Password"), message, ct);
    }

    private string? Setting(string key) =>
        _config.Settings.TryGetValue(key, out var v) ? v : null;

    internal static SecureSocketOptions ParseTls(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "starttlswhenavailable" => SecureSocketOptions.StartTlsWhenAvailable,
        _ => SecureSocketOptions.Auto,
    };

    internal static bool IsTransient(Exception ex) => ex switch
    {
        SmtpCommandException sce => (int)sce.StatusCode is >= 400 and < 500,
        SmtpProtocolException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        _ => false,
    };
}
