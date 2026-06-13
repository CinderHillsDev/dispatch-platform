using Dispatch.Core.Providers;

namespace Dispatch.Providers;

/// <summary>
/// Dev-mode provider (spec §8.5): accepts and discards every message, reporting success.
/// Message content is still recorded in the log/UI as if delivered, so no real email is sent.
/// </summary>
public sealed class NoneProvider : IRelayProvider
{
    public string Name => "None";

    public Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct) =>
        Task.FromResult(RelayResult.Success(
            id: $"none-{Guid.NewGuid():N}",
            detail: "Discarded (dev/none mode)"));
}
