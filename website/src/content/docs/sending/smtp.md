---
title: SMTP listener
description: Accept mail over SMTP with STARTTLS, optional AUTH, and a source-IP allow-list.
sidebar:
  order: 1
---

Dispatch listens for SMTP on the standard ports **25** and **587** by default. Point any app, device,
or appliance that speaks SMTP at it.

Binding 25/587 needs elevation — the installers and the appliance run with it (the systemd unit is
granted `CAP_NET_BIND_SERVICE`; Windows runs as a service; the container runs as root). If port **25**
can't be bound — it's already in use, or the process lacks privilege — the listener automatically
falls back to **2525** (587 is still used when it's free). You'll see this in the logs at startup.

:::tip[Recommended]
Install Dispatch on a host with **no other SMTP software** (Postfix, Sendmail, Exim, …) so ports 25
and 587 are free. Otherwise Dispatch can't take them and falls back to 2525.
:::

## Key settings

| Setting | Default | Notes |
|---|---|---|
| Ports | `25, 587` | Comma-separated; falls back to `2525` if 25 can't be bound |
| Bind address | `0.0.0.0` | Listens on all interfaces (filtered by the allow-list below) |
| Allowed IPs / CIDRs | loopback + private ranges | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` so it isn't an open relay; add your subnet, or clear to allow all |
| Require AUTH | `false` | Require username/password from senders ([SMTP authentication](/configuration/overview/)) |
| Max connections | `100` | Concurrent connection cap |
| Connection timeout | `60s` | Per-command timeout |

## STARTTLS

The listener offers **STARTTLS** out of the box: if you haven't configured a
[shared TLS certificate](/configuration/tls-certificate/), it uses an auto-generated self-signed one
(the same cert the HTTPS API and dashboard use). Configure or upload a CA-issued shared cert to replace
it so senders trust it. By default AUTH is only offered after STARTTLS, so credentials are never sent
in the clear.

## Access control

The SMTP listener is **closed by default** to anything outside loopback and private ranges, so a
fresh install is never an open relay. Add your sender subnets under **Settings → Connections**, or
clear the list to allow all (not recommended on a public interface). Denied connections are logged
with their source IP. See [Security](/security/) for the full model.
