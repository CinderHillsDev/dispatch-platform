---
title: TLS certificate
description: Manage the single shared TLS certificate that secures SMTP STARTTLS and the optional HTTPS ingestion API.
sidebar:
  order: 3
---

Dispatch uses **one shared TLS certificate** that secures both the SMTP STARTTLS upgrade and the optional HTTPS ingestion API (port `8026`). The dashboard has its own separate certificate and is not affected by this setting.

## Managing the shared certificate

Manage the shared cert under **Settings → Connections → TLS certificate**. You have two options:

- **Generate** a self-signed certificate directly in the dashboard. This is convenient for testing and internal networks.
- **Upload** your own PFX file (for example, a cert issued by a public or internal CA). The cert password is stored encrypted at rest.

The selected source is recorded in the `tls.cert_source` config key (`"generated"` or `"uploaded"`); the path and password are held in `tls.cert_path` and `tls.cert_password`. See the [config key reference](/configuration/config-keys/) for details.

## What requires a certificate

Until a certificate is configured:

- **SMTP STARTTLS is unavailable.** Clients connecting on the listener ports cannot upgrade to an encrypted session. See [SMTP relay](/sending/smtp/).
- **The HTTPS ingestion API cannot be enabled.** The `api.tls_enabled` toggle has no effect without a cert. See [HTTP API](/sending/http-api/).

## TLS version

Dispatch enforces a **TLS 1.2 minimum** floor for both the SMTP STARTTLS upgrade and the HTTPS API. Older protocol versions are refused.

For broader hardening guidance, see [Security](/security/).
