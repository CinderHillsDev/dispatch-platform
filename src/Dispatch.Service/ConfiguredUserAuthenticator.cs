using System.Net;
using Dispatch.Core.Audit;
using Dispatch.Core.Smtp;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Net;

namespace Dispatch.Service;

/// <summary>SMTP AUTH against the configured credential allow-list (spec §5.3, §19.2), with a per-source-IP
/// brute-force lockout (spec §17.10): once an IP is locked out, AUTH is refused without hitting the store.</summary>
public sealed class ConfiguredUserAuthenticator(ISmtpCredentialRepository credentials, SmtpAuthThrottle throttle, IAuditLog? audit = null)
    : IUserAuthenticator
{
    public async Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
    {
        var ip = RemoteIp(context)?.ToString() ?? "unknown";
        if (throttle.IsLocked(ip))
        {
            if (audit is not null) await audit.Audit("SmtpAuth", "SMTP AUTH blocked (locked out)", "Warning", actor: user, sourceIp: ip);
            return false;
        }

        var ok = await credentials.VerifyAsync(user, password, cancellationToken);
        if (ok) throttle.RecordSuccess(ip);
        else
        {
            throttle.RecordFailure(ip);
            if (audit is not null) await audit.Audit("SmtpAuth", $"SMTP AUTH failed for \"{user}\"", "Warning", actor: user, sourceIp: ip);
        }
        return ok;
    }

    private static IPAddress? RemoteIp(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) && ep is IPEndPoint ipep
            ? ipep.Address
            : null;
}
