using Dispatch.Core.Providers;

namespace Dispatch.Core.Relays;

/// <summary>A relay's provider type plus its generic provider settings (keys match what providers read).</summary>
public sealed record RelaySettings(RelayProviderType Provider, IReadOnlyDictionary<string, string?> Settings)
{
    public static RelaySettings Empty { get; } = new(RelayProviderType.Unconfigured, new Dictionary<string, string?>());
}

/// <summary>Describes one configurable field for a provider (drives validation, encryption, and the UI form).</summary>
public sealed record RelayFieldDescriptor(string Name, string ConfigSuffix, bool Secret, bool Required);

/// <summary>The per-provider field schema. Maps generic setting names to SQL <c>config</c> key suffixes (§12.3).</summary>
public static class RelayProviderSchema
{
    public static IReadOnlyList<RelayFieldDescriptor> For(RelayProviderType provider) => provider switch
    {
        RelayProviderType.Mailgun =>
        [
            new("ApiKey", "mailgun.api_key", Secret: true, Required: true),
            new("Domain", "mailgun.domain", Secret: false, Required: true),
            new("Region", "mailgun.region", Secret: false, Required: false),
        ],
        RelayProviderType.SendGrid =>
        [
            new("ApiKey", "sendgrid.api_key", Secret: true, Required: true),
        ],
        RelayProviderType.AzureCommunication =>
        [
            new("ConnectionString", "azure.connection_string", Secret: true, Required: true),
            // One field for the verified MailFrom address(es). Storage suffix kept as the legacy
            // "azure.sender_address" so existing relays' configured sender carries over unchanged.
            new("MailFrom", "azure.sender_address", Secret: false, Required: true),
        ],
        RelayProviderType.Smtp =>
        [
            new("Host", "smtp.host", Secret: false, Required: true),
            new("Port", "smtp.port", Secret: false, Required: false),
            new("Username", "smtp.username", Secret: false, Required: false),
            new("Password", "smtp.password", Secret: true, Required: false),
            new("TlsMode", "smtp.tls_mode", Secret: false, Required: false),
        ],
        RelayProviderType.AmazonSes =>
        [
            new("AccessKeyId", "ses.access_key_id", Secret: false, Required: true),
            new("SecretAccessKey", "ses.secret_access_key", Secret: true, Required: true),
            new("Region", "ses.region", Secret: false, Required: true),
        ],
        RelayProviderType.Postmark =>
        [
            new("ApiKey", "postmark.server_token", Secret: true, Required: true),
            new("MessageStream", "postmark.message_stream", Secret: false, Required: false),
        ],
        RelayProviderType.Resend =>
        [
            new("ApiKey", "resend.api_key", Secret: true, Required: true),
        ],
        RelayProviderType.SparkPost =>
        [
            new("ApiKey", "sparkpost.api_key", Secret: true, Required: true),
            new("Region", "sparkpost.region", Secret: false, Required: false),
        ],
        RelayProviderType.Smtp2Go =>
        [
            new("ApiKey", "smtp2go.api_key", Secret: true, Required: true),
        ],
        RelayProviderType.Maileroo =>
        [
            new("ApiKey", "maileroo.api_key", Secret: true, Required: true),
        ],
        RelayProviderType.Bird =>
        [
            new("ApiKey", "bird.api_key", Secret: true, Required: true),
            new("WorkspaceId", "bird.workspace_id", Secret: false, Required: true),
            new("ChannelId", "bird.channel_id", Secret: false, Required: true),
        ],
        _ => [],
    };
}

/// <summary>Reads/writes a relay's provider + credentials from the SQL config table (spec §12.3).</summary>
public interface IRelaySettingsStore
{
    Task<RelaySettings> GetAsync(int relayId, CancellationToken ct = default);
    Task SaveAsync(int relayId, RelaySettings settings, CancellationToken ct = default);
}
