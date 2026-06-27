---
title: Generic SMTP
description: Relay mail to any SMTP smart host from Dispatch SMTP Relay.
sidebar:
  order: 12
---

The Generic SMTP provider relays to any SMTP smart host — Office 365, Postfix, AWS SES SMTP, and similar. Add it from the dashboard's **Relays** page, choose **SMTP**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch delivers over SMTP using MailKit, sending the raw MIME message. The envelope recipients, CC, BCC, and attachments are all preserved — see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting  | Required | Description                                                          |
| -------- | -------- | ------------------------------------------------------------------- |
| Host     | Yes      | The SMTP server hostname.                                           |
| Port     | Yes      | The SMTP port (default `25`).                                       |
| Username | Yes      | The login username. Encrypted at rest.                              |
| Password | Yes      | The login password. Encrypted at rest.                              |
| TLS mode | Yes      | One of `None`, `Auto`, `StartTls`, or `SslOnConnect`.               |

## Notes

- Choose the **TLS mode** that matches your host: `StartTls` (often port 587), `SslOnConnect` (often port 465), `Auto` to negotiate, or `None` for plaintext.
- For custom certificates, see [TLS certificate configuration](/configuration/tls-certificate/).
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
