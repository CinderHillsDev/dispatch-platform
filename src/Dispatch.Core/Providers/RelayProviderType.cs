namespace Dispatch.Core.Providers;

/// <summary>Upstream relay provider types (spec §8).</summary>
public enum RelayProviderType
{
    /// <summary>No provider chosen yet — the out-of-the-box default. Dispatch refuses to relay until a
    /// provider is configured, so mail is never silently delivered or discarded.</summary>
    Unconfigured = 0,

    /// <summary>Explicit dev mode: accepts and discards mail, logging it as delivered (spec §8.5).</summary>
    None,
    Smtp,
    Mailgun,
    SendGrid,
    AzureCommunication,
}
