---
title: Local (capture mode)
description: Capture messages to the Local Inbox for development and CI instead of delivering them.
sidebar:
  order: 13
---

The Local provider is a developer capture mode. It performs **no external delivery** - every message routed to it is captured to the [Local Inbox](/sending/local-inbox/) for inspection. Add it from the dashboard's **Relays** page and choose **Local**. See the [provider overview](/providers/overview/) for how relays fit into routing.

## Settings

The Local provider has **no credentials or settings** to configure. There is nothing to authenticate because nothing leaves the server.

## Setup

- Select **Local** as the provider when creating the relay, then save - no further configuration is needed.
- Route the addresses or domains you want to capture to this relay (see [routing](/routing/)).
- Send a **test email** from the dashboard and watch the relay log live; the message appears in the [Local Inbox](/sending/local-inbox/) rather than being delivered.

## Notes

- Ideal for **development and CI**: exercise your application's email paths without sending real mail or needing provider credentials.
- Captured messages retain everything described in [message features](/sending/message-features/) - multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments - so you can inspect exactly what would have been sent.
