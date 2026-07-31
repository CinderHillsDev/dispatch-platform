using Dispatch.Core.Providers;
using Dispatch.Providers;
using MimeKit;

namespace Dispatch.Providers.Tests;

// Microsoft 365's endpoint is tenant-specific, so unlike GoogleWorkspaceProvider there's a required setting
// to validate. The guard runs before any network I/O, so this is testable without a live server.
public class Microsoft365ProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_throws_when_host_is_not_configured(string? host)
    {
        var settings = new Dictionary<string, string?> { ["Host"] = host };
        var config = new RelayConfig { Provider = RelayProviderType.Microsoft365, Settings = settings };
        var provider = new Microsoft365Provider(config);
        var message = new RelayMessage { Message = new MimeMessage(), FromAddress = "a@example.com", ToAddresses = ["b@example.com"] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SendAsync(message, CancellationToken.None));
    }

    [Fact]
    public void Name_is_Microsoft365()
    {
        var config = new RelayConfig { Provider = RelayProviderType.Microsoft365 };
        Assert.Equal("Microsoft365", new Microsoft365Provider(config).Name);
    }
}
