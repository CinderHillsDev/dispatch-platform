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

        var accepted = await filter.CanAcceptFromAsync(
            ctx, new Mailbox("alice", "example.com"), size: 10, CancellationToken.None);

        Assert.False(accepted);
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

    private static CidrMailboxFilter Build(long relayMaxBytes, IntakeState? intake = null)
    {
        var resolver = new StubRelayResolver(new ResolvedRelay
        {
            Config = new RelayConfig { Id = 1, Name = "small-relay", MaxMessageBytes = relayMaxBytes },
        });
        return new CidrMailboxFilter(
            Options.Create(new ListenerOptions { AllowedCidrs = ["0.0.0.0/0", "::/0"] }),
            new InMemoryCounterRepository(),
            resolver,
            intake ?? new IntakeState(),
            NullLogger<CidrMailboxFilter>.Instance);
    }
}
