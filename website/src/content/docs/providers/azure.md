---
title: Azure Communication Services
description: Relay mail through Azure Communication Services from Dispatch SMTP Relay.
sidebar:
  order: 11
---

Azure Communication Services (ACS) Email is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Azure Communication Services**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through ACS using the official Azure SDK. All standard message capabilities are supported — see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting           | Required | Description                                                              |
| ----------------- | -------- | ----------------------------------------------------------------------- |
| Connection String | Yes      | The ACS connection string. Encrypted at rest.                           |
| MailFrom          | Yes      | One or more explicit sender addresses configured in your ACS resource.  |

## Notes

- Unlike most providers, **ACS has no domain concept** — you define an explicit **MailFrom** list. Each sender you intend to use must be a configured MailFrom address.
- You can define **multiple MailFrom addresses** on a single relay.
- When you send a **test email** from the dashboard, the From field is a dropdown of the MailFrom addresses configured for that relay. Pick one, send, and watch the relay log live to confirm delivery.
