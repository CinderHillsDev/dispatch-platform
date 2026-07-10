namespace Dispatch.Core.Routing;

/// <summary>Selects the relay to use for a message based on sender/recipient (spec §10).</summary>
public interface IRelayResolver
{
    ValueTask<ResolvedRelay> ResolveAsync(
        string fromAddress,
        IReadOnlyList<string> toAddresses,
        CancellationToken ct = default);
}
