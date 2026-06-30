---
title: Providers overview
description: The relay providers Dispatch supports and what each needs to be configured.
sidebar:
  order: 1
  label: Overview
---

A **relay** is an upstream destination Dispatch forwards mail to. You can configure several and pick
between them with [routing rules](/routing/). Every provider supports **multiple recipients, CC, BCC,
text + HTML bodies, custom headers, and attachments**.

## Supported providers

| Provider | Mechanism | Required settings |
|---|---|---|
| [Mailgun](https://mailgun.com) | REST API (raw MIME) | API Key, Domain, Region (US/EU) |
| [SendGrid](https://sendgrid.com) | Web API v3 (SDK) | API Key |
| [Amazon SES](https://aws.amazon.com/ses/) | SES v2 API (SDK, raw MIME) | Access Key ID, Secret Access Key, Region |
| [Postmark](https://postmarkapp.com) | REST API | Server Token (Message Stream optional) |
| [Resend](https://resend.com) | REST API | API Key |
| [SparkPost](https://www.sparkpost.com) | REST API | API Key (Region optional) |
| [SMTP2GO](https://www.smtp2go.com) | REST API | API Key |
| [Maileroo](https://maileroo.com) | REST API | Sending Key |
| [Bird](https://bird.com) | REST API | API credentials |
| [Azure Communication Services](https://azure.microsoft.com/products/communication-services) | SDK | Connection String, Sender Address |
| Generic **SMTP** | SMTP via MailKit | Host, Port, Username, Password, TLS mode |
| **Local** | Capture only | - captures to the [Local Inbox](/sending/local-inbox/); never delivers |

## Notes

- **Raw-MIME providers** (Mailgun, Amazon SES, generic SMTP) preserve the original message including
  arbitrary custom headers.
- **Azure Communication Services** requires each sender to be a configured **MailFrom** address; the
  dashboard test lets you pick from the MailFrom addresses defined for that relay.
- The **Local** provider is for development and CI - see [Local Inbox](/sending/local-inbox/).

More providers are welcome - see the
[provider issues](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3Aprovider) and
the [Contributing](/project/contributing/) guide.
