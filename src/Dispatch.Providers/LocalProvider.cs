using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Local / developer-mode provider (spec §8.5). Never opens a network connection or delivers to the
/// outside world. When a capture directory is configured, each message is written there as an
/// <c>.eml</c> file so developers can inspect it (and view it in the Local Inbox); otherwise the
/// message is accepted and discarded. Either way it is logged as delivered.
/// </summary>
public sealed class LocalProvider(string? captureDirectory = null) : IRelayProvider
{
    public string Name => "Local";

    public async Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(captureDirectory))
            return RelayResult.Success(id: $"local-{Guid.NewGuid():N}", detail: "Discarded (local/dev mode - no external delivery)");

        Directory.CreateDirectory(captureDirectory);
        var name = (message.SpoolId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")) + ".eml";
        var path = Path.Combine(captureDirectory, name);
        await using (var fs = File.Create(path))
            await message.Message.WriteToAsync(fs, ct);

        return RelayResult.Success(id: name, detail: $"Captured locally to {path} (no external delivery)");
    }
}
