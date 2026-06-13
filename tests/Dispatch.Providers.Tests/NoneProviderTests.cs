using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

public class NoneProviderTests
{
    private static RelayMessage Message()
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("dev@local.test"));
        mime.To.Add(MailboxAddress.Parse("someone@example.com"));
        mime.Subject = "local";
        mime.Body = new TextPart("plain") { Text = "captured, not sent" };
        return new RelayMessage { Message = mime, FromAddress = "dev@local.test", ToAddresses = ["someone@example.com"], SpoolId = "abc123" };
    }

    [Fact]
    public async Task Captures_message_to_local_directory_without_sending()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-capture-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new NoneProvider(dir).SendAsync(Message(), default);

            var files = Directory.GetFiles(dir, "*.eml");
            Assert.Single(files);
            Assert.Contains("someone@example.com", await File.ReadAllTextAsync(files[0]));
            Assert.Contains("Captured locally", result.ProviderDetail);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Discards_when_no_capture_directory()
    {
        var result = await new NoneProvider().SendAsync(Message(), default);
        Assert.NotNull(result.ProviderMessageId);
        Assert.Contains("no external delivery", result.ProviderDetail);
    }
}
