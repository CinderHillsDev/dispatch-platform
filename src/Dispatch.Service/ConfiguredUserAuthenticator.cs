using Dispatch.Core.Smtp;
using SmtpServer;
using SmtpServer.Authentication;

namespace Dispatch.Service;

/// <summary>SMTP AUTH against the configured credential allow-list (spec §5.3, §19.2).</summary>
public sealed class ConfiguredUserAuthenticator(ISmtpCredentialRepository credentials) : IUserAuthenticator
{
    public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken) =>
        credentials.VerifyAsync(user, password, cancellationToken);
}
