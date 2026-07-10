using System.Net;
using Dispatch.Core.Smtp;
using Dispatch.Service;

namespace Dispatch.Core.Tests;

public class ConfiguredUserAuthenticatorTests
{
    private sealed class CountingCreds : ISmtpCredentialRepository
    {
        public int Calls;
        public bool Result;
        public Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
        public Task<IReadOnlyList<SmtpCredential>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<SmtpCredential>)[]);
        public Task AddAsync(string username, string password, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string username, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task Locked_ip_is_refused_without_hitting_the_credential_store()
    {
        var creds = new CountingCreds { Result = false };
        var auth = new ConfiguredUserAuthenticator(creds, new SmtpAuthThrottle());
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 5000));

        for (var i = 0; i < 5; i++)
            Assert.False(await auth.AuthenticateAsync(ctx, "user", "bad", CancellationToken.None));
        Assert.Equal(5, creds.Calls);   // five real credential checks

        // 6th attempt: the IP is now locked, so it is refused without consulting the store.
        Assert.False(await auth.AuthenticateAsync(ctx, "user", "bad", CancellationToken.None));
        Assert.Equal(5, creds.Calls);
    }

    [Fact]
    public async Task Valid_credentials_authenticate()
    {
        var creds = new CountingCreds { Result = true };
        var auth = new ConfiguredUserAuthenticator(creds, new SmtpAuthThrottle());
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 5000));

        Assert.True(await auth.AuthenticateAsync(ctx, "user", "good", CancellationToken.None));
    }
}
