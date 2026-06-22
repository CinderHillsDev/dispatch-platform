// Credential field schema per provider, mirrored from RelayProviderSchema (Core). Shared by the Relays
// page and the first-run wizard so the credential forms stay in sync.
export interface ProviderField { name: string; secret: boolean; required: boolean }

export const PROVIDER_FIELDS: Record<string, ProviderField[]> = {
  Local: [],
  Smtp: [
    { name: "Host", secret: false, required: true },
    { name: "Port", secret: false, required: false },
    { name: "Username", secret: false, required: false },
    { name: "Password", secret: true, required: false },
    { name: "TlsMode", secret: false, required: false },
  ],
  Mailgun: [
    { name: "ApiKey", secret: true, required: true },
    { name: "Domain", secret: false, required: true },
    { name: "Region", secret: false, required: false },
  ],
  SendGrid: [{ name: "ApiKey", secret: true, required: true }],
  AzureCommunication: [
    { name: "ConnectionString", secret: true, required: true },
    { name: "SenderAddress", secret: false, required: true },
  ],
  AmazonSes: [
    { name: "AccessKeyId", secret: false, required: true },
    { name: "SecretAccessKey", secret: true, required: true },
    { name: "Region", secret: false, required: true },
  ],
  Postmark: [
    { name: "ApiKey", secret: true, required: true },
    { name: "MessageStream", secret: false, required: false },
  ],
  Resend: [{ name: "ApiKey", secret: true, required: true }],
  SparkPost: [
    { name: "ApiKey", secret: true, required: true },
    { name: "Region", secret: false, required: false },
  ],
  Smtp2Go: [{ name: "ApiKey", secret: true, required: true }],
  Maileroo: [{ name: "ApiKey", secret: true, required: true }],
};

// Friendly labels for the provider picker (enum name -> display name).
export const PROVIDER_LABELS: Record<string, string> = {
  Mailgun: "Mailgun",
  SendGrid: "SendGrid",
  AmazonSes: "Amazon SES",
  Postmark: "Postmark",
  Resend: "Resend",
  SparkPost: "SparkPost",
  Smtp2Go: "SMTP2GO",
  Maileroo: "Maileroo",
  AzureCommunication: "Azure Communication Services",
  Smtp: "Generic SMTP host",
  Local: "Local (developer capture — no external delivery)",
};
