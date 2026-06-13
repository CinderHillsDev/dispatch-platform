using Dispatch.Core.Spool;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using System.Buffers;
using System.Net;
using System.Text;

namespace Dispatch.Core.Tests;

public class SpoolMessageStoreTests
{
    [Fact]
    public async Task SaveAsync_writes_eml_and_meta_signals_and_returns_ok()
    {
        using var t = new TempSpool();
        var store = new SpoolMessageStore(t.Spool, NullLogger<SpoolMessageStore>.Instance);

        var raw = TestData.SampleEml(from: "alice@example.com", to: "bob@example.org", tag: "welcome");
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(raw));
        var ctx = new FakeSessionContext(new IPEndPoint(IPAddress.Loopback, 4242));
        var tx = new FakeMessageTransaction(
            new Mailbox("alice", "example.com"),
            [new Mailbox("bob", "example.org")]);

        var response = await store.SaveAsync(ctx, tx, buffer, CancellationToken.None);

        Assert.Equal(SmtpResponse.Ok, response);

        // .eml written
        var emls = Directory.GetFiles(t.Spool.IncomingDir, "*.eml");
        Assert.Single(emls);

        // .meta written with envelope, source IP, and the parsed tag
        var meta = SpoolMeta.Load(emls[0]);
        Assert.Equal("alice@example.com", meta.FromAddress);
        Assert.Equal(["bob@example.org"], meta.ToAddresses);
        Assert.Equal("127.0.0.1", meta.SourceIp);
        Assert.Equal("SMTP", meta.IngestSource);
        Assert.NotNull(meta.Tags);
        Assert.Contains("welcome", meta.Tags!);

        // doorbell was rung
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var signaled = await t.Spool.WaitAsync(cts.Token);
        Assert.Equal(Path.GetFileName(emls[0]), signaled);
    }
}

// ---- Minimal SmtpServer fakes (only the members SpoolMessageStore touches) ----------------

internal sealed class FakeMessageTransaction(IMailbox from, IList<IMailbox> to) : IMessageTransaction
{
    public IMailbox From { get; set; } = from;
    public IList<IMailbox> To { get; } = to;
    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string>();
}

#pragma warning disable CS0067 // events required by the interface but never raised in tests
internal sealed class FakeSessionContext : ISessionContext
{
    public FakeSessionContext(IPEndPoint remote)
    {
        Properties = new Dictionary<string, object> { [EndpointListener.RemoteEndPointKey] = remote };
    }

    public IDictionary<string, object> Properties { get; }

    public IServiceProvider ServiceProvider => throw new NotImplementedException();
    public IEndpointDefinition EndpointDefinition => throw new NotImplementedException();
    public Guid SessionId => default;
    public ISecurableDuplexPipe? Pipe { get; set; }
    public ISmtpServerOptions ServerOptions => throw new NotImplementedException();
    public AuthenticationContext Authentication { get; set; } = AuthenticationContext.Unauthenticated;

    public event EventHandler<SmtpCommandEventArgs>? CommandExecuting;
    public event EventHandler<SmtpCommandEventArgs>? CommandExecuted;
    public event EventHandler<SmtpResponseExceptionEventArgs>? ResponseException;
    public event EventHandler<EventArgs>? SessionAuthenticated;
}
#pragma warning restore CS0067
