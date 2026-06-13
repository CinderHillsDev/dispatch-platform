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

        // RCPT TO runs routing and rejects before DATA.
        Assert.False(await filter.CanDeliverToAsync(ctx, to, from, CancellationToken.None));
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

    private static CidrMailboxFilter Build(long relayMaxBytes, IntakeState? intake = null,
        Dispatch.Service.ConnectionTracker? connections = null, int maxConnections = 0)
    {
        var resolver = new StubRelayResolver(new ResolvedRelay
        {
            Config = new RelayConfig { Id = 1, Name = "small-relay", MaxMessageBytes = relayMaxBytes },
        });
        var values = new Dictionary<string, string>
        {
            [ConfigKeys.ListenerAllowedCidrs] = "[\"0.0.0.0/0\",\"::/0\"]",
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
        connections.Increment();   // 2 — over a cap of 1
        var filter = Build(relayMaxBytes: 0, connections: connections, maxConnections: 1);
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));

        var ex = await Assert.ThrowsAsync<SmtpServer.Protocol.SmtpResponseException>(() =>
            filter.CanAcceptFromAsync(ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None));
        Assert.Equal(SmtpServer.Protocol.SmtpReplyCode.ServiceUnavailable, ex.Response.ReplyCode);
    }
}
