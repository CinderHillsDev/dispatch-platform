namespace Dispatch.Core.Providers;

/// <summary>Upstream relay provider types (spec §8). Only None and Smtp are implemented in milestone 1.</summary>
public enum RelayProviderType
{
    None = 0,
    Smtp,
    Mailgun,
    SendGrid,
    AzureCommunication,
}
