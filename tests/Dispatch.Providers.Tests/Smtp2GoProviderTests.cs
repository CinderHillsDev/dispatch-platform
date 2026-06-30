using System.Net;
using Dispatch.Core.Providers;
using Dispatch.Providers;

namespace Dispatch.Providers.Tests;

public class Smtp2GoProviderTests
{
    private static RelayConfig Config() => new()
    {
        Provider = RelayProviderType.Smtp2Go,
        Settings = new Dictionary<string, string?> { ["ApiKey"] = "api-secret" },
    };

    [Fact]
    public async Task Posts_to_v3_send_with_key_in_body_and_parses_email_id()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"data\":{\"succeeded\":1,\"failed\":0,\"email_id\":\"sg-123\"}}");
        var result = await new Smtp2GoProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.Message(), default);

        Assert.Equal("https://api.smtp2go.com/v3/email/send", handler.RequestUri);
        Assert.Contains("\"api_key\":\"api-secret\"", handler.Body);   // key travels in the JSON body
        Assert.Contains("rcpt@dest.com", handler.Body);
        Assert.Equal("sg-123", result.ProviderMessageId);
    }

    [Fact]
    public async Task Splits_to_cc_and_keeps_bcc_out_of_visible_fields()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"data\":{\"succeeded\":1,\"email_id\":\"sg-1\"}}");
        await new Smtp2GoProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithCcBcc(), default);

        Assert.Contains("\"to\":[\"to@dest.com\"]", handler.Body);
        Assert.Contains("\"cc\":[\"cc@dest.com\"]", handler.Body);
        Assert.Contains("\"bcc\":[\"bcc@hidden.com\"]", handler.Body);
    }

    [Fact]
    public async Task Forwards_attachments_as_fileblob()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{\"data\":{\"succeeded\":1,\"failed\":0,\"email_id\":\"sg-1\"}}");
        await new Smtp2GoProvider(Config(), new HttpClient(handler))
            .SendAsync(ProviderTestSupport.MessageWithAttachment(), default);

        Assert.Contains("\"attachments\"", handler.Body);
        Assert.Contains("\"filename\":\"hello.txt\"", handler.Body);
        Assert.Contains("\"fileblob\":\"" + ProviderTestSupport.AttachmentBase64 + "\"", handler.Body);
    }

    [Fact]
    public async Task Http_200_with_error_in_body_is_permanent_failure()
    {
        // SMTP2GO returns 200 even when it rejects the message - the error lives in data.error.
        var handler = new StubHttpHandler(HttpStatusCode.OK,
            "{\"data\":{\"error\":\"Sender not verified\",\"error_code\":\"E_ApiResponseCodes.NON_VALIDATING_IN_PAYLOAD\",\"field_validation_errors\":{\"field\":\"sender\"}}}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Smtp2GoProvider(Config(), new HttpClient(handler)).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Contains("Sender not verified", ex.Message);
    }

    [Fact]
    public async Task Http_200_with_zero_succeeded_is_permanent_failure()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK,
            "{\"data\":{\"succeeded\":0,\"failed\":1,\"failures\":[\"bad@recipient\"]}}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Smtp2GoProvider(Config(), new HttpClient(handler)).SendAsync(ProviderTestSupport.Message(), default));
        Assert.Contains("succeeded=0", ex.Message);
    }

    [Fact]
    public async Task Server_error_is_transient()
    {
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.InternalServerError, "boom"));
        await Assert.ThrowsAsync<TransientRelayException>(() =>
            new Smtp2GoProvider(Config(), http).SendAsync(ProviderTestSupport.Message(), default));
    }

    [Fact]
    public async Task Missing_api_key_is_permanent()
    {
        var cfg = new RelayConfig { Provider = RelayProviderType.Smtp2Go, Settings = new Dictionary<string, string?>() };
        var http = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Smtp2GoProvider(cfg, http).SendAsync(ProviderTestSupport.Message(), default));
    }
}
