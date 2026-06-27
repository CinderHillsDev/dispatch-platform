---
title: Security
description: Dispatch's security model — authentication, access control, encryption, and TLS.
---

Dispatch is designed to be safe by default: a fresh install is never an open relay, secrets are
encrypted at rest, and the dashboard is HTTPS-only.

## Authentication

- **Dashboard** — a single admin password, set on first run and stored only as a **bcrypt** hash
  (cost 12). Cookie sessions; repeated failed logins trigger a **per-IP throttle**.
- **HTTP API** — per-key bearer tokens (`dsp_live_…`), stored as bcrypt hashes and verified in
  constant time, with per-key rate limiting. Revocation is immediate.
- **SMTP** — optional AUTH against a username/password allow-list (passwords bcrypt-hashed); by
  default AUTH is refused over an unencrypted connection.

## Access control (CIDR allow-lists)

Application-layer source-IP allow-lists gate every listener:

- **SMTP** and the **HTTP API** are **closed by default** — only loopback and private/RFC1918 ranges
  — so a fresh install can't be abused as an open relay.
- The **dashboard** is **open by default** (gated by the admin password) so you can reach the
  first-run setup; restrict `webui.allowed_cidrs` to lock it down.

Denied connections are logged with their source IP.

## Encryption & TLS

- **Secrets at rest** — provider API keys, SMTP credentials, and the TLS cert password are encrypted
  with **AES-256-GCM** (Linux/macOS, key in `.dispatch-key`, mode 600) or **DPAPI** (Windows).
- **Shared TLS certificate** — one generated or uploaded cert secures both SMTP STARTTLS and the
  HTTPS API (TLS 1.2+). The dashboard uses its own cert (configured or auto self-signed). See
  [TLS certificate](/configuration/tls-certificate/).

## Health & metrics

`/health` and `/metrics` are unauthenticated but served only on the dashboard port and gated by its
IP allow-list, and they expose no secrets or message content. See
[Health & metrics](/operations/health-metrics/).

## Reporting a vulnerability

Please report security issues privately via
[GitHub Security Advisories](https://github.com/chrismuench/Dispatch-SMTP-Relay/security/advisories/new)
rather than a public issue.
