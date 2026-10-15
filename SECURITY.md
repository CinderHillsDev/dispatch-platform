# Security Policy

Dispatch SMTP Relay handles mail and provider credentials, so security reports are taken seriously and
very welcome.

## Reporting a vulnerability

**Please report privately - do not open a public issue for a vulnerability.**

Use **[GitHub Security Advisories](https://github.com/CinderHillsDev/dispatch-platform/security/advisories/new)**
(Security → Report a vulnerability). That keeps the report private until a fix is available and lets us
collaborate on it directly.

If you can, include:

- A description of the issue and its impact
- Steps to reproduce (a minimal proof of concept is ideal)
- Affected version / commit, and your environment (OS, deployment shape - installer, Docker, etc.)
- Any suggested remediation

Please give us a reasonable window to release a fix before public disclosure. We'll acknowledge your
report, keep you updated on progress, and credit you in the advisory unless you'd prefer to stay anonymous.

## Supported versions

Dispatch is pre-1.0 and moving quickly. Security fixes target the **latest release** and the default
branch. Please reproduce on the latest version before reporting.

## Scope

In scope - anything that lets an attacker:

- Send mail through Dispatch without authorization (open-relay / allow-list bypass / SMTP AUTH bypass)
- Read or exfiltrate message content, the spool, provider credentials, or API keys
- Bypass the dashboard login, API-key auth, or the source-IP allow-lists
- Escalate privileges via the installers/service, or inject SQL / commands
- Cause persistent denial of service

Generally out of scope: findings that require an already-compromised host or physical access; missing
hardening headers with no demonstrated impact; volumetric DoS; and social-engineering reports.

## Where security controls live (for reviewers)

- Source-IP allow-lists: `WebAuthMiddleware`, `ApiKeyMiddleware`, `CidrMailboxFilter`
- Auth & throttling: `AuthEndpoints` + `LoginThrottle` (dashboard), `ConfiguredUserAuthenticator` +
  `SmtpAuthThrottle` (SMTP AUTH), `SqlApiKeyRepository` (bcrypt, constant-time verify)
- Credential encryption at rest: `SecureConfig` (AES-256-GCM on Linux/macOS, DPAPI on Windows) - including
  the dashboard's own `webui.password_hash` row, rewritten encrypted on startup for hosts upgrading from
  builds that stored it in the clear
- Transport: `WebUi:TlsCertPath` enables dashboard HTTPS

## Dependency monitoring

Three package ecosystems are watched by Dependabot (`.github/dependabot.yml`): `github-actions`, the
`npm` graph in `src/Dispatch.UI`, and the `nuget` graph. Grouped updates keep one PR per ecosystem per week.

Two gaps are worth knowing about, because they hide real vulnerabilities:

- **Transitive NuGet packages.** Without a committed `packages.lock.json`, Dependabot only sees the
  `PackageReference` entries in the `.csproj` files, not what they pull in. A vulnerable transitive has to
  be floored with a direct pin - see `Directory.Build.props` for the current example, and run
  `dotnet list Dispatch.slnx package --vulnerable --include-transitive` to check the whole graph locally.
- **Majors held back on purpose.** The EF Core family is pinned to the major the database providers are
  built against (see the comment in `.github/dependabot.yml`). Holding a major back is a decision with an
  expiry date, not a permanent state.

For the npm graph, `overrides` in `src/Dispatch.UI/package.json` floor transitive advisories (build-time
packages such as `postcss`, `source-map-js`, `browserslist` and `baseline-browser-mapping` are reachable
through `vite`/`rollup` and are not always surfaced as Dependabot alerts). `npm audit` in that directory is
the check.

See [`docs/SPEC.md` §17](docs/SPEC.md) for the full security model.
