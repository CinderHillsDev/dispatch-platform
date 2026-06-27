---
title: SMTP listener
description: Accept mail over SMTP with STARTTLS, optional AUTH, and a source-IP allow-list.
sidebar:
  order: 1
---

Dispatch listens for SMTP on configurable ports (**2525** by default; set **25**/**587** for
production, which require elevated privileges). Point any app, device, or appliance that speaks SMTP
at it.

## Key settings

| Setting | Default | Notes |
|---|---|---|
| Ports | `2525` | Comma-separated; set `25,587` for production |
| Bind address | `0.0.0.0` | Listens on all interfaces (filtered by the allow-list below) |
| Allowed IPs / CIDRs | loopback + private ranges | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` so it isn't an open relay; add your subnet, or clear to allow all |
| Require AUTH | `false` | Require username/password from senders ([SMTP authentication](/configuration/overview/)) |
| Max connections | `100` | Concurrent connection cap |
| Connection timeout | `60s` | Per-command timeout |

## STARTTLS

The listener upgrades to TLS via **STARTTLS** when a [TLS certificate](/configuration/tls-certificate/)
is configured — the same shared certificate also secures the HTTPS API. Until a cert is set, the
listener accepts plaintext (and, by default, refuses AUTH over an unencrypted connection).

## Access control

The SMTP listener is **closed by default** to anything outside loopback and private ranges, so a
fresh install is never an open relay. Add your sender subnets under **Settings → Connections**, or
clear the list to allow all (not recommended on a public interface). Denied connections are logged
with their source IP. See [Security](/security/) for the full model.
