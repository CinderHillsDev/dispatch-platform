# Contributing to Dispatch

Thanks for your interest in improving Dispatch! It's a self-hosted, open-source email relay (Apache-2.0),
and contributions of all kinds are welcome: bug reports, features, providers, docs, and tests.

## Ways to contribute

- **Report a bug** or request a feature via [GitHub Issues](https://github.com/CinderHillsDev/dispatch-platform/issues).
  Please include your version/commit, deployment shape (Docker, installer, appliance), and repro steps.
- **Fix something** and open a pull request (see the flow below).
- **Add a relay provider** - see [Adding a provider](README.md#adding-a-provider).
- **Improve the docs** - those live in the separate [`dispatch-docs`](https://github.com/CinderHillsDev/dispatch-docs) repo.
- **Report a security issue** - please do this privately; see [SECURITY.md](SECURITY.md).

## Development setup

You need the **.NET 10 SDK**, **Node.js 20+**, and **Docker** (for a local PostgreSQL). The full build/run
steps are in the README under [Building from source](README.md#building-from-source). In short:

```bash
docker compose up -d                       # PostgreSQL; schema auto-created on first run
cd src/Dispatch.UI && npm install && npm run build && cd ../..
rm -rf src/Dispatch.Web/wwwroot && mkdir -p src/Dispatch.Web/wwwroot
cp -r src/Dispatch.UI/dist/* src/Dispatch.Web/wwwroot/
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Dispatch.Service
```

`appsettings.Development.json` (git-ignored) needs at least the PostgreSQL connection string and an
`AdminPassword`.

## Running the tests

```bash
dotnet test                                # unit tests always run
```

The `Dispatch.Data` integration tests run against a real PostgreSQL only when `DISPATCH_TEST_SQL` is set
(they auto-skip otherwise). To run them locally:

```bash
docker run -d -e POSTGRES_PASSWORD=devpass -p 5432:5432 postgres:17
export DISPATCH_TEST_SQL="Host=localhost;Port=5432;Username=postgres;Password=devpass"
dotnet test
```

Please make sure `dotnet test` is green before opening a PR.

## Pull request flow

1. Fork the repo and create a topic branch off `main`.
2. Make focused changes with clear commit messages; match the style, naming, and comment density of the
   surrounding code.
3. Add or update tests for anything you change.
4. Ensure `dotnet test` passes and the app builds.
5. Open a PR describing **what** changed and **why**. Link any related issue.

CI (GitHub Actions) builds the solution, runs the tests against PostgreSQL, and validates the installers.
A green CI run is required to merge.

## Code style

- Match the existing code: same formatting, naming, and comment conventions as the file you're editing.
- **Use ASCII hyphens (`-`) only - never em dashes (U+2014).** A CI check rejects em dashes because they
  break Windows PowerShell parsing.
- Keep the data layer behind its repository interfaces; all SQL lives in `Dispatch.Data`.

## License

By contributing, you agree that your contributions are licensed under the project's
[Apache License 2.0](LICENSE).
