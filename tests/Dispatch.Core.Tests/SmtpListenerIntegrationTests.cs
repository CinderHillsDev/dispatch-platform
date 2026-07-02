using System.Net;
using System.Net.Sockets;
using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;
using Dispatch.Service;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Dispatch.Core.Tests;

// End-to-end inbound SMTP: actually start the listener on a loopback port and drive it with a real SMTP
// client. Covers what the unit tests can't - STARTTLS via the self-signed fallback, message acceptance into
// the spool, and the source-IP allow-list enforced over the wire. Complements the slow installer smoke with
// a fast, in-process regression guard.
public sealed class SmtpListenerIntegrationTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class TestHostEnv(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Dispatch.Core.Tests";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static SmtpListenerService BuildListener(TempSpool spool, int port, string allowedCidrs)
    {
        var cache = new ConfigCache();
        cache.LoadFrom(new Dictionary<string, string>
        {
            [ConfigKeys.ListenerAllowedCidrs] = allowedCidrs,
        });

        var messageStore = new SpoolMessageStore(spool.Spool, cache, NullLogger<SpoolMessageStore>.Instance);
        var filter = new CidrMailboxFilter(
            cache,
            new InMemoryCounterRepository(),
            new StubRelayResolver(new ResolvedRelay
            {
                Config = new RelayConfig { Id = 1, Name = "default" },
            }),
            new Dispatch.Core.Licensing.LicenseGate(),
            new IntakeState(),
            new ConnectionTracker(),
            new CapturingLogRepository(),
            new Dispatch.Core.Logging.AlwaysLogSettings(),
            NullLogger<CidrMailboxFilter>.Instance);
        var auth = new ConfiguredUserAuthenticator(new NoCreds(), new SmtpAuthThrottle());

        var options = Options.Create(new ListenerOptions { Ports = [port] });
        return new SmtpListenerService(
            messageStore, filter, auth, new ConnectionTracker(), options,
            new SmtpListenerState(), new TestHostEnv(spool.Root), NullLogger<SmtpListenerService>.Instance);
    }

    private sealed class NoCreds : Dispatch.Core.Smtp.ISmtpCredentialRepository
    {
        public Task<bool> VerifyAsync(string u, string p, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Dispatch.Core.Smtp.SmtpCredential>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<Dispatch.Core.Smtp.SmtpCredential>)[]);
        public Task AddAsync(string u, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string u, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static async Task WaitUntilListening(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) { await Task.Delay(50); }
        }
        throw new TimeoutException($"SMTP listener did not start on port {port} within {timeout}.");
    }

    private static MimeMessage SampleMessage()
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse("alice@example.com"));
        msg.To.Add(MailboxAddress.Parse("bob@example.org"));
        msg.Subject = "integration test";
        msg.Body = new TextPart("plain") { Text = "hello over SMTP" };
        return msg;
    }

    [Fact]
    public async Task Message_received_over_smtp_with_starttls_lands_in_spool()
    {
        using var spool = new TempSpool();
        var port = FreePort();
        var svc = BuildListener(spool, port, allowedCidrs: "[\"127.0.0.1/32\",\"::1/128\"]");
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListening(port, TimeSpan.FromSeconds(10));

            using var client = new SmtpClient
            {
                // Accept the self-signed STARTTLS cert the listener generates when no shared cert is configured.
                ServerCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = 15_000,
            };
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.StartTls);
            Assert.True(client.IsSecure);   // STARTTLS actually negotiated
            await client.SendAsync(SampleMessage());
            await client.DisconnectAsync(true);

            // The message is written to the incoming spool before delivery (the 250 OK is given after the write).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (spool.Count(spool.Spool.IncomingDir) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Equal(1, spool.Count(spool.Spool.IncomingDir));
        }
        finally
        {
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Source_ip_outside_allow_list_is_rejected()
    {
        using var spool = new TempSpool();
        var port = FreePort();
        // Allow only a private range that does NOT include loopback, so the test client (127.0.0.1) is refused.
        var svc = BuildListener(spool, port, allowedCidrs: "[\"10.0.0.0/8\"]");
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListening(port, TimeSpan.FromSeconds(10));

            using var client = new SmtpClient
            {
                ServerCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = 15_000,
            };
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.StartTls);
            // MAIL FROM is refused for a disallowed source IP → the send throws and nothing is spooled.
            await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(SampleMessage()));
            try { await client.DisconnectAsync(true); } catch { /* connection may already be torn down */ }

            Assert.Equal(0, spool.Count(spool.Spool.IncomingDir));
        }
        finally
        {
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
        }
    }
}
