namespace Dispatch.Core.Providers;

/// <summary>Outcome of a successful upstream delivery.</summary>
public sealed class RelayResult
{
    public string? ProviderMessageId { get; init; }
    public string? ProviderDetail { get; init; }

    public static RelayResult Success(string? id = null, string? detail = null) =>
        new() { ProviderMessageId = id, ProviderDetail = detail };
}
