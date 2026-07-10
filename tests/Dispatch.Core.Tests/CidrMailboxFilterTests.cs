using Dispatch.Core.Configuration;
using Dispatch.Core.Counters;
using Dispatch.Core.Maintenance;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Service;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using System.Net;

namespace Dispatch.Core.Tests;

public class CidrMailboxFilterTests
{
    [Fact]
    public async Task CanDeliverTo_rejects_when_declared_size_exceeds_per_relay_limit()
    {
        var filter = Build(relayMaxBytes: 100);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));
        var from = new Mailbox("alice", "example.com");
        var to = new Mailbox("bob", "example.org");

        // MAIL FROM declares a SIZE= larger than the relay's limit; no global ceiling configured.
        Assert.True(await filter.CanAcceptFromAsync(ctx, from, size: 500, CancellationToken.None));

        // RCPT TO runs routing and rejects before DATA with 552 (size limit exceeded), not a generic 550.
        var ex = await Assert.ThrowsAsync<SmtpResponseException>(() =>
            filter.CanDeliverToAsync(ctx, to, from, CancellationToken.None));
        Assert.Equal(SmtpReplyCode.SizeLimitExceeded, ex.Response.ReplyCode);
    }

    [Fact]
    public async Task CanDeliverTo_allows_when_declared_size_within_per_relay_limit()
    {
        var filter = Build(relayMaxBytes: 100);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));
        var from = new Mailbox("alice", "example.com");
        var to = new Mailbox("bob", "example.org");

        Assert.True(await filter.CanAcceptFromAsync(ctx, from, size: 50, CancellationToken.None));
        Assert.True(await filter.CanDeliverToAsync(ctx, to, from, CancellationToken.None));
    }

    [Fact]
    public async Task CanDeliverTo_allows_when_no_size_declared()
    {
        var filter = Build(relayMaxBytes: 100);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));
        var from = new Mailbox("alice", "example.com");
        var to = new Mailbox("bob", "example.org");

        Assert.True(await filter.CanAcceptFromAsync(ctx, from, size: 0, CancellationToken.None));
        Assert.True(await filter.CanDeliverToAsync(ctx, to, from, CancellationToken.None));
    }

    [Fact]
    public async Task CanAcceptFrom_rejects_when_intake_suspended()
    {
        var intake = new IntakeState();
        intake.Apply(IntakeState.SuspendBytes - 1);   // critically low → Suspended
        var filter = Build(relayMaxBytes: 0, intake: intake);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));

        // Suspended intake yields a transient 452 (SmtpResponseException) so senders retry (spec §14.1).
        var ex = await Assert.ThrowsAsync<SmtpServer.Protocol.SmtpResponseException>(() =>
            filter.CanAcceptFromAsync(ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
        Assert.Equal(SmtpServer.Protocol.SmtpReplyCode.InsufficientStorage, ex.Response.ReplyCode);
    }

    [Fact]
    public async Task CanAcceptFrom_delays_then_accepts_when_intake_throttled()
    {
        var intake = new IntakeState();
        intake.Apply(IntakeState.ThrottleBytes - 1);   // low → Throttled
        var filter = Build(relayMaxBytes: 0, intake: intake);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));

        var sw = Stopwatch.StartNew();
        var accepted = await filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None);
        sw.Stop();

        Assert.True(accepted);
        Assert.True(sw.Elapsed >= IntakeState.ThrottleDelay - TimeSpan.FromMilliseconds(250),
            $"expected throttle delay, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task CanAcceptFrom_rejects_source_ip_outside_allow_list()
    {
        // The listener default ships loopback + private ranges; a public source IP must be refused so the
        // SMTP listener is not an open relay (spec §1.1 / §17.10).
        var filter = Build(relayMaxBytes: 0, allowedCidrs: ConfigPrivateRanges);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 4242));

        // Rejection is a 550 5.7.1 access-denied (clear reason), not the generic "mailbox unavailable".
        var ex = await Assert.ThrowsAsync<SmtpResponseException>(() => filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
        Assert.Equal(SmtpReplyCode.MailboxUnavailable, ex.Response.ReplyCode);
        Assert.Contains("Access denied", ex.Response.Message);
    }

    [Theory]
    [InlineData("172.17.0.1")]   // Docker bridge gateway
    [InlineData("10.4.5.6")]     // private LAN
    [InlineData("192.168.1.20")] // home/office LAN
    [InlineData("127.0.0.1")]    // same-host app
    public async Task CanAcceptFrom_allows_private_and_loopback_sources(string ip)
    {
        var filter = Build(relayMaxBytes: 0, allowedCidrs: ConfigPrivateRanges);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Parse(ip), 4242));

        Assert.True(await filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
    }

    [Fact]
    public async Task CanAcceptFrom_denies_all_when_allow_list_empty()
    {
        // Closed model (spec §17.10): an empty allow-list denies everyone - even a private/loopback source -
        // so an unconfigured listener is never an open relay. Allowing all requires explicit 0.0.0.0/0 + ::/0.
        var filter = Build(relayMaxBytes: 0, allowedCidrs: "[]");
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4242));

        var ex = await Assert.ThrowsAsync<SmtpResponseException>(() => filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
        Assert.Equal(SmtpReplyCode.MailboxUnavailable, ex.Response.ReplyCode);
    }

    [Fact]
    public async Task CanAcceptFrom_allows_all_when_default_route_present()
    {
        // The explicit "allow all" opt-in: 0.0.0.0/0 + ::/0 lets an operator intentionally open the listener.
        var filter = Build(relayMaxBytes: 0, allowedCidrs: "[\"0.0.0.0/0\",\"::/0\"]");
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 4242));

        Assert.True(await filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
    }

    // The real seeded listener default (kept in sync with ConfigDefaults by ConfigDefaultsTests).
    private const string ConfigPrivateRanges =
        "[\"127.0.0.1/32\",\"::1/128\",\"10.0.0.0/8\",\"172.16.0.0/12\",\"192.168.0.0/16\",\"fc00::/7\"]";

    private static CidrMailboxFilter Build(long relayMaxBytes, IntakeState? intake = null,
        Dispatch.Service.ConnectionTracker? connections = null, int maxConnections = 0,
        string? allowedCidrs = null)
    {
        var resolver = new StubRelayResolver(new ResolvedRelay
        {
            Config = new RelayConfig { Id = 1, Name = "small-relay", MaxMessageBytes = relayMaxBytes },
        });
        var values = new Dictionary<string, string>
        {
            [ConfigKeys.ListenerAllowedCidrs] = allowedCidrs ?? "[\"0.0.0.0/0\",\"::/0\"]",
        };
        if (maxConnections > 0) values[ConfigKeys.ListenerMaxConnections] = maxConnections.ToString();
        var cache = new ConfigCache();
        cache.LoadFrom(values);
        return new CidrMailboxFilter(
            cache,
            new InMemoryCounterRepository(),
            resolver,
            intake ?? new IntakeState(),
            connections ?? new Dispatch.Service.ConnectionTracker(),
            new CapturingLogRepository(),
            new Dispatch.Core.Logging.AlwaysLogSettings(),
            NullLogger<CidrMailboxFilter>.Instance);
    }

    [Fact]
    public async Task CanAcceptFrom_rejects_when_over_connection_cap()
    {
        var connections = new Dispatch.Service.ConnectionTracker();
        connections.Increment();   // 1
        connections.Increment();   // 2 - over a cap of 1
        var filter = Build(relayMaxBytes: 0, connections: connections, maxConnections: 1);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));

        var ex = await Assert.ThrowsAsync<SmtpServer.Protocol.SmtpResponseException>(() =>
            filter.CanAcceptFromAsync(ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
        Assert.Equal(SmtpServer.Protocol.SmtpReplyCode.ServiceUnavailable, ex.Response.ReplyCode);
    }

}
