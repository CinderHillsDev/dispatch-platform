namespace Dispatch.Core.Providers;

/// <summary>
/// A resolved relay configuration. In milestone 1 it is built from <see cref="Configuration.DefaultRelayOptions"/>;
/// later it is loaded from the SQL <c>relays</c> table (spec §6.11, §8).
/// </summary>
public sealed class RelayConfig
{
    public int Id { get; init; } = 1;
    public string Name { get; init; } = "default";
    public RelayProviderType Provider { get; init; } = RelayProviderType.Unconfigured;
    public int MaxConcurrency { get; init; } = 4;
    public long MaxMessageBytes { get; init; } = 0;
    public IReadOnlyDictionary<string, string?> Settings { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>The size ceiling actually enforced: the configured value, or the provider-type default.</summary>
    public long EffectiveMaxMessageBytes =>
        MaxMessageBytes > 0 ? MaxMessageBytes : ProviderDefaultMaxBytes(Provider);

    private static long ProviderDefaultMaxBytes(RelayProviderType t) => t switch
    {
        RelayProviderType.Mailgun => 25L * 1024 * 1024,
        RelayProviderType.SendGrid => 30L * 1024 * 1024,
        RelayProviderType.AzureCommunication => 10L * 1024 * 1024,
        _ => 0,
    };
}
