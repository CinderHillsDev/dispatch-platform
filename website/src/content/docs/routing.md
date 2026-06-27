---
title: Routing
description: Route messages to different relays by sender and recipient domain.
---

Routing rules decide **which relay** handles a given message. With several
[relays](/providers/overview/) configured, rules let you send (for example) `*.marketing.example.com`
through one provider and everything else through another.

## How rules match

- Each rule matches on a **sender domain** pattern and/or a **recipient domain** pattern (glob-style,
  `*` wildcard).
- Rules are evaluated by **priority** (ascending), then **specificity** (a rule with both patterns is
  more specific than one with a single pattern or none).
- Only **enabled** rules are considered; disabled rules are skipped.
- If no rule matches, the **default relay** (the catch-all) handles the message.

## Managing rules

In the dashboard under **Routing** you can create, reorder, enable/disable, and delete rules. A
**simulate** tool resolves a sample from/to pair against the current rules so you can confirm the
outcome before sending anything live.
