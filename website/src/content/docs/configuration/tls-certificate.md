---
title: TLS certificate
description: Manage the single shared TLS certificate that secures SMTP STARTTLS and the optional HTTPS ingestion API.
sidebar:
  order: 3
---

Dispatch uses **one shared TLS certificate** that secures both the SMTP STARTTLS upgrade and the optional HTTPS ingestion API (port `8026`). The dashboard has its own separate certificate and is not affected by this setting.

**TLS works out of the box.** You don't have to configure anything to get encryption: if no shared certificate is set, Dispatch generates and persists a **self-signed** certificate automatically and uses it for SMTP STARTTLS and the HTTPS API (the dashboard self-signs the same way). Configure a certificate here only to **replace** that self-signed cert with one your clients trust — typically a CA-issued cert for production.

## Managing the shared certificate

Manage the shared cert under **Settings → Connections → TLS certificate**. You have two options:

- **Generate** a self-signed certificate directly in the dashboard. This is convenient for testing and internal networks.
- **Upload** your certificate and private key as two **PEM files** — the certificate (`.pem`/`.crt`) and its **unencrypted** private key. There's no PFX and **no password to enter**: Dispatch packages the two into a PFX itself, generates a random password for it, and stores that **encrypted at rest**. (If your key is passphrase-protected, decrypt it first.)

The selected source is recorded in the `tls.cert_source` config key (`"generated"` or `"uploaded"`); the path and the auto-generated password are held in `tls.cert_path` and `tls.cert_password`. See the [config key reference](/configuration/config-keys/) for details.

## Self-signed vs. configured

- **No certificate configured (default):** SMTP STARTTLS and the HTTPS API both use an auto-generated, persisted **self-signed** certificate. Encryption works, but clients that validate certificates will see an untrusted-cert warning. Fine for testing and internal networks.
- **Certificate configured (generate or upload):** that certificate replaces the self-signed one for both the SMTP STARTTLS upgrade and the HTTPS API. Upload a CA-issued cert so clients trust it without warnings.

See [SMTP relay](/sending/smtp/) and [HTTP API](/sending/http-api/).

## TLS version

Dispatch enforces a **TLS 1.2 minimum** floor for both the SMTP STARTTLS upgrade and the HTTPS API. Older protocol versions are refused.

For broader hardening guidance, see [Security](/security/).
