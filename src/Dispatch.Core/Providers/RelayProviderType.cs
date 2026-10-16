namespace Dispatch.Core.Providers;

/// <summary>Upstream relay provider types (spec §8).</summary>
public enum RelayProviderType
{
    /// <summary>No provider chosen yet - the out-of-the-box default. Dispatch refuses to relay until a
    /// provider is configured, so mail is never silently delivered or discarded.</summary>
    Unconfigured = 0,

    /// <summary>Local / developer mode: never delivers externally; captures mail to spool/captured/ for
    /// inspection and logs it as delivered (spec §8.5).</summary>
    Local,
    Smtp,
    Mailgun,
    SendGrid,
    AzureCommunication,
    AmazonSes,
    Postmark,
    Resend,
    SparkPost,
    Smtp2Go,
    Maileroo,
    Bird,

    /// <summary>Direct, unauthenticated delivery to Google Workspace's unified inbound mail endpoint
    /// (smtp.google.com). Every Workspace domain shares this same endpoint, so there is nothing per-domain
    /// to configure - only use it for domains actually hosted on Google Workspace.</summary>
    GoogleWorkspace,

    /// <summary>Direct, unauthenticated delivery to a Microsoft 365 tenant's inbound mail endpoint
    /// (&lt;tenant&gt;.mail.protection.outlook.com). Unlike Google Workspace this is tenant-specific, so the
    /// hostname must be typed in.</summary>
    Microsoft365,
}
