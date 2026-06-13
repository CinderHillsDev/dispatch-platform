# Contributing to Dispatch

Thanks for your interest in improving Dispatch.

## Development setup

See **[Building & running locally](README.md#building--running-locally)** in the README — in short:
`docker compose up -d`, build the UI into `Dispatch.Web/wwwroot`, then
`ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Dispatch.Service`.

Requirements: .NET 10 SDK, Node.js 20+, Docker.

## Running tests

```bash
# Unit + API tests (no database):
dotnet test

# Include the SQL integration tests against your local container:
DISPATCH_TEST_SQL="Server=localhost,1433;User Id=sa;Password=Dispatch_Dev_Pass123;TrustServerCertificate=True;Encrypt=True" \
  dotnet test
```

The `Dispatch.Data.Tests` integration tests auto-skip when `DISPATCH_TEST_SQL` is unset, so the suite
stays green without a database. CI runs them against a SQL service container.

## Project layout

See the [Project Structure](README.md#project-structure) section. Key boundaries:

- **Dispatch.Core** — pure domain (spool pipeline, worker pool, routing, interfaces). No SQL, no ASP.NET.
- **Dispatch.Data** — SQL implementations of the Core repository interfaces.
- **Dispatch.Web** — HTTP/SignalR endpoints, ingestion, auth, embedded UI.
- **Dispatch.Service** — the host that composes everything.

## Conventions

- Match the surrounding style; keep comments focused on *why*, not *what*.
- Prefer adding a test with each behavioral change. Security/correctness-sensitive code
  (routing, auth, API keys, SQL queries) should have direct tests.
- SQL queries always parameterise values; filter clauses are built from fixed fragments only.
- Don't break the hot path: nothing on the SMTP→`250 OK` path may touch SQL or the network.

## Adding a relay provider

Implement `IRelayProvider` in `Dispatch.Providers`, add a `RelayProviderType` enum value, wire it in
`RelayProviderFactory`, and add its field schema to `RelayProviderSchema` (Core) so the UI renders the
credential form. See `MailgunProvider` / `SendGridProvider` for reference, and add tests like
`MailgunProviderTests` (a stubbed transport, no live calls).

## Pull requests

Work on a branch, keep commits focused, ensure `dotnet test` is green, and open a PR. CI must pass.
