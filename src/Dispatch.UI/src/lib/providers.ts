// Credential field schema per provider, mirrored from RelayProviderSchema (Core). Shared by the Relays
// page and the first-run wizard so the credential forms stay in sync. `options` renders a dropdown (e.g.
// regions) instead of a free-text box; `placeholder` is an optional input hint.
export interface ProviderField { name: string; secret: boolean; required: boolean; options?: string[]; placeholder?: string }

// Common AWS region codes for Amazon SES (the SES region is an AWS region, not a US/EU choice).
const AWS_REGIONS = [
  "us-east-1", "us-east-2", "us-west-1", "us-west-2",
  "eu-west-1", "eu-west-2", "eu-central-1", "eu-north-1",
  "ap-south-1", "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ca-central-1", "sa-east-1",
];

export const PROVIDER_FIELDS: Record<string, ProviderField[]> = {
  Local: [],
  Smtp: [
    { name: "Host", secret: false, required: true, placeholder: "smtp.example.com" },
    { name: "Port", secret: false, required: false, placeholder: "587" },
    { name: "Username", secret: false, required: false },
    { name: "Password", secret: true, required: false },
    { name: "TlsMode", secret: false, required: false, options: ["Auto", "StartTls", "SslOnConnect", "None"] },
  ],
  Mailgun: [
    { name: "ApiKey", secret: true, required: true },
    { name: "Domain", secret: false, required: true, placeholder: "mg.example.com" },
    { name: "Region", secret: false, required: false, options: ["US", "EU"] },
  ],
  SendGrid: [{ name: "ApiKey", secret: true, required: true }],
  AzureCommunication: [
    { name: "ConnectionString", secret: true, required: true },
    { name: "SenderAddress", secret: false, required: true, placeholder: "DoNotReply@your-domain.azurecomm.net" },
  ],
  AmazonSes: [
    { name: "AccessKeyId", secret: false, required: true },
    { name: "SecretAccessKey", secret: true, required: true },
    { name: "Region", secret: false, required: true, options: AWS_REGIONS },
  ],
  Postmark: [
    { name: "ApiKey", secret: true, required: true },
    { name: "MessageStream", secret: false, required: false, placeholder: "outbound" },
  ],
  Resend: [{ name: "ApiKey", secret: true, required: true }],
  SparkPost: [
    { name: "ApiKey", secret: true, required: true },
    { name: "Region", secret: false, required: false, options: ["US", "EU"] },
  ],
  Smtp2Go: [{ name: "ApiKey", secret: true, required: true }],
  Maileroo: [{ name: "ApiKey", secret: true, required: true }],
  Bird: [
    { name: "ApiKey", secret: true, required: true },
    { name: "WorkspaceId", secret: false, required: true },
    { name: "ChannelId", secret: false, required: true, placeholder: "your email channel id" },
  ],
};

// Brand-colored monogram per provider (self-contained, no external/trademarked logo assets) for the
// provider cards. `fg` overrides the text color on light backgrounds.
export const PROVIDER_BRAND: Record<string, { bg: string; fg?: string; mark: string }> = {
  Mailgun: { bg: "#C02126", mark: "MG" },
  SendGrid: { bg: "#1A82E2", mark: "SG" },
  AmazonSes: { bg: "#FF9900", fg: "#1a1a1a", mark: "SES" },
  Postmark: { bg: "#FFDD33", fg: "#1a1a1a", mark: "PM" },
  Resend: { bg: "#111827", mark: "RS" },
  SparkPost: { bg: "#FA6423", mark: "SP" },
  Bird: { bg: "#2A21E5", mark: "BD" },
  Smtp2Go: { bg: "#00A4E4", mark: "S2" },
  Maileroo: { bg: "#4F46E5", mark: "ML" },
  AzureCommunication: { bg: "#0078D4", mark: "AZ" },
  Smtp: { bg: "#64748B", mark: "@" },
  Local: { bg: "#475569", mark: "DEV" },
};

// Per-provider "where do I get this?" help links, shown in the add-relay wizard.
export const PROVIDER_DOCS: Record<string, string> = {
  Mailgun: "https://documentation.mailgun.com/docs/mailgun/api-reference/intro/",
  SendGrid: "https://app.sendgrid.com/settings/api_keys",
  AmazonSes: "https://docs.aws.amazon.com/ses/latest/dg/send-email-smtp.html",
  Postmark: "https://postmarkapp.com/support/article/1008-what-are-the-account-and-server-api-tokens",
  Resend: "https://resend.com/api-keys",
  SparkPost: "https://app.sparkpost.com/account/api-keys",
  Smtp2Go: "https://app.smtp2go.com/settings/api-keys",
  Maileroo: "https://maileroo.com/docs/email-api/introduction",
  Bird: "https://docs.bird.com/api/channels-api",
  AzureCommunication: "https://learn.microsoft.com/azure/communication-services/quickstarts/email/send-email",
};

// Display order for provider pickers (real deliverable providers first; Local/SMTP last).
export const PROVIDER_ORDER = [
  "Mailgun", "SendGrid", "AmazonSes", "Postmark", "Resend", "SparkPost", "Bird", "Smtp2Go", "Maileroo", "AzureCommunication", "Smtp", "Local",
];

// Friendly labels for the provider picker (enum name -> display name).
export const PROVIDER_LABELS: Record<string, string> = {
  Mailgun: "Mailgun",
  SendGrid: "SendGrid",
  AmazonSes: "Amazon SES",
  Postmark: "Postmark",
  Resend: "Resend",
  SparkPost: "SparkPost",
  Bird: "Bird",
  Smtp2Go: "SMTP2GO",
  Maileroo: "Maileroo",
  AzureCommunication: "Azure Communication Services",
  Smtp: "Generic SMTP host",
  Local: "Local (developer capture — no external delivery)",
};
