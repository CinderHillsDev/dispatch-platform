---
title: Amazon SES
description: Relay mail through Amazon SES v2 from Dispatch SMTP Relay.
sidebar:
  order: 4
---

Amazon Simple Email Service (SES) is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Amazon SES**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through the SES v2 API using the official AWS SDK, submitting the raw MIME message. Because the original MIME is preserved, all of your headers and attachments pass through exactly as built - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting           | Required | Description                                            |
| ----------------- | -------- | ------------------------------------------------------ |
| Access Key ID     | Yes      | The IAM access key ID. Encrypted at rest.              |
| Secret Access Key | Yes      | The IAM secret access key. Encrypted at rest.          |
| Region            | Yes      | The AWS region of your SES account (e.g. `us-east-1`). |

## Notes

- Use an IAM user or role with the `ses:SendRawEmail` / SES v2 send permission.
- Verify your sending identity (domain or address) in SES, and remember new accounts start in the SES sandbox.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
