---
title: Contributing
description: How to contribute to Dispatch, including adding a new relay provider.
sidebar:
  order: 1
---

Contributions are welcome. Please read
[CONTRIBUTING.md](https://github.com/chrismuench/Dispatch-SMTP-Relay/blob/main/CONTRIBUTING.md)
before opening a PR.

**Good first issues:** provider implementations, UI improvements, documentation. See the
[issue tracker](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3A%22good+first+issue%22).

## Adding a provider

1. Implement `IRelayProvider` in `Dispatch.Providers` (see `SendGridProvider.cs` as the reference).
2. Add the provider's settings model and a case in `RelayProviderFactory`.
3. Add the UI fields in the provider settings page.
4. Add tests in `Dispatch.Providers.Tests`.
5. Document it on the [Providers overview](/providers/overview/).
