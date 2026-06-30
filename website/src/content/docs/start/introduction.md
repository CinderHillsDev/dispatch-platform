---
title: Introduction
description: What Dispatch SMTP Relay is, why it exists, and how it works at a glance.
sidebar:
  order: 1
---

**Dispatch SMTP Relay** is an open-source, self-hosted .NET service that sits between your
applications and your email provider. Point your apps and devices at Dispatch over **SMTP** (or a
Mailgun-compatible **HTTP API**); it queues every message durably and forwards it to a cloud
provider - Mailgun, SendGrid, Amazon SES, Postmark, Resend, SparkPost, SMTP2GO, Maileroo, Bird,
Azure Communication Services, or any SMTP smart host - with a live web dashboard to monitor,
configure, and troubleshoot everything.

## Why Dispatch?

Most applications need to send email. Wiring every app directly to a cloud provider means scattered
credentials, no central log, and no fallback when a provider has an outage. Dispatch sits in the
middle:

- **`250 OK` before anything else** - Dispatch writes each message to a local spool file and
  acknowledges the sender immediately. No database, no HTTP call on the hot path. SQL Server is
  written *after* the provider accepts the message.
- **The spool directory is the queue** - `.eml` files survive restarts, crashes, and SQL outages.
  If SQL Server is down, mail still flows.
- **One place for credentials** - rotate an API key once, not in every app.
- **Full message log** - after-the-fact history in SQL Server, searchable in the dashboard.
- **Test before you commit** - verify provider credentials with a live relay log, or capture mail
  to the [Local Inbox](/sending/local-inbox/) without delivering anything.

## Where to next

- [Quickstart](/start/quickstart/) - running in a few minutes with Docker.
- [How it works](/start/how-it-works/) - the spool pipeline and data flow.
- [Deployment](/deployment/overview/) - Docker, Linux, Windows, or a ready-to-import appliance.
