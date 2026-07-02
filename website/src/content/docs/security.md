---
title: Security
description: Dispatch's security model - authentication, access control, encryption, and TLS.
---

Dispatch is designed to be safe by default: a fresh install is never an open relay, secrets are
encrypted at rest, and the dashboard is HTTPS-only.

## Authentication

- **Dashboard** - a single admin password, set on first run and stored only as a **bcrypt** hash
  (cost 12). Cookie sessions; repeated failed logins trigger a **per-IP throttle**.
- **HTTP API** - per-key bearer tokens (`dsp_live_…`), stored as bcrypt hashes and verified in
  constant time, with per-key rate limiting. Revocation is immediate.
- **SMTP** - optional AUTH against a username/password allow-list (passwords bcrypt-hashed); by
  default AUTH is refused over an unencrypted connection.

## Access control (CIDR allow-lists)

Application-layer source-IP allow-lists gate every listener:

- **SMTP** and the **HTTP API** are **closed by default** - only loopback and private/RFC1918 ranges
  - so a fresh install can't be abused as an open relay.
- The **dashboard** is **open by default** (gated by the admin password) so you can reach the
  first-run setup; restrict `webui.allowed_cidrs` to lock it down.

Denied connections are logged with their source IP.

## Encryption & TLS

- **Secrets at rest** - provider API keys, SMTP credentials, and the TLS cert password are encrypted
  with **AES-256-GCM** using a random 256-bit key in a `.dispatch-key` file (mode 600 on Linux/macOS;
  on Windows it sits in the data dir under `C:\ProgramData\Dispatch`, which the installer ACL-locks to
  SYSTEM + Administrators only - covering the connection string, spool, key, and logs together). The key
  is **portable** - a database backup can be restored on a different host by also restoring the key file.
  See [Backup & restore](/operations/backup-restore/).
- **Shared TLS certificate** - one generated or uploaded cert secures both SMTP STARTTLS and the
  HTTPS API (TLS 1.2+). If none is configured, both fall back to an auto-generated self-signed cert, so
  TLS is available out of the box (the dashboard self-signs the same way). Configure a CA-issued cert to
  remove untrusted-cert warnings. See [TLS certificate](/configuration/tls-certificate/).

## Privacy - no call home

Dispatch never phones home. There is **no telemetry, no analytics, no usage stats, and no automatic
update polling** anywhere in the software. Dispatch is commercially licensed, but license verification
is **fully offline** - the key is validated locally against an embedded public key and node-locked to
this install; nothing is transmitted to us. The only outbound network connections it makes are to the
**mail providers you configure** (to deliver mail) - nothing about your install, your configuration, or
your messages is sent to us or any third party. Updates are
applied only from a **signed package you upload yourself** through the dashboard; nothing runs in the
background reaching out for new versions. Dispatch runs fully on an isolated network as long as your
chosen provider is reachable.

## Health & metrics

`/health` and `/metrics` are unauthenticated but served only on the dashboard port and gated by its
IP allow-list, and they expose no secrets or message content. See
[Health & metrics](/operations/health-metrics/).

## Reporting a vulnerability

Please report security issues privately by email to **security@dispatchrelay.app** rather than in public.
