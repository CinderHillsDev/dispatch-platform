namespace Dispatch.Core.Providers;

/// <summary>
/// Upstream relay provider (spec §8). A new instance is built per dispatch from a
/// <see cref="RelayConfig"/> so multiple named relays of the same type can coexist.
/// Throw <see cref="TransientRelayException"/> for retryable failures; any other exception is permanent.
/// </summary>
public interface IRelayProvider
{
    string Name { get; }
    Task<RelayResult> SendAsync(RelayMessage message, CancellationToken ct);
}
