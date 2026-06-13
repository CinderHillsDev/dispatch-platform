using System.Text;
using Dispatch.Core.Configuration;
using Dispatch.Core.Providers;
using Dispatch.Core.Routing;
using Dispatch.Core.Spool;
using Dispatch.Web.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Tests;

public class ApiMessageHandlerTests
{
    [Fact]
    public async Task Oversized_message_is_rejected_with_413()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-apihandler", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new ApiMessageHandler(
                new SpoolDirectory(dir),
                Options.Create(new ApiOptions { MaxMessageBytes = 10 }),
                Resolver(maxBytes: 0),
                NullLogger<ApiMessageHandler>.Instance);

            var json = """{"from":"a@x.com","to":["b@y.com"],"subject":"hi","text":"well over ten bytes"}""";
            var result = await handler.HandleAsync(JsonRequest(json), default);

            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
            Assert.Empty(Directory.GetFiles(new SpoolDirectory(dir).IncomingDir, "*.eml"));   // nothing spooled
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Message_exceeding_per_relay_limit_is_rejected_with_413()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-apihandler", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new ApiMessageHandler(
                new SpoolDirectory(dir),
                Options.Create(new ApiOptions { MaxMessageBytes = 0 }),   // no global ceiling
                Resolver(maxBytes: 10),                                   // tiny per-relay limit
                NullLogger<ApiMessageHandler>.Instance);

            var json = """{"from":"a@x.com","to":["b@y.com"],"subject":"hi","text":"well over ten bytes"}""";
            var result = await handler.HandleAsync(JsonRequest(json), default);

            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
            Assert.Empty(Directory.GetFiles(new SpoolDirectory(dir).IncomingDir, "*.eml"));   // nothing spooled
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpContext JsonRequest(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = body.Length;
        return ctx;
    }

    private static IRelayResolver Resolver(long maxBytes) => new StubResolver(
        new ResolvedRelay { Config = new RelayConfig { Id = 1, Name = "test", MaxMessageBytes = maxBytes } });

    private sealed class StubResolver(ResolvedRelay relay) : IRelayResolver
    {
        public ValueTask<ResolvedRelay> ResolveAsync(
            string fromAddress, IReadOnlyList<string> toAddresses, CancellationToken ct = default) =>
            ValueTask.FromResult(relay);
    }
}
