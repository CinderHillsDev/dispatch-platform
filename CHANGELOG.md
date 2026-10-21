# Changelog

All notable changes to Dispatch SMTP Relay are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Download any release from the [Releases page](https://github.com/CinderHillsDev/dispatch-platform/releases).

> **A note on 0.6.** There is no public 0.6 release. `0.6` was an internal development line used while the
> database layer was rebuilt; it was never tagged or published. The public sequence goes **0.5 → 0.7**. A
> 0.5 install upgrades directly to 0.7 - see the upgrade notes below.

## [Unreleased]

### Fixed

- **Deleting a routing rule that had already matched a message failed with a database error.** `relay_log`
  carries a foreign key to `routing_rules`, so the dashboard's delete threw `FOREIGN KEY constraint failed`
  (reported on SQLite, and the same on the other three backends) as soon as a rule had routed one message.
  A rule that had never matched deleted cleanly, which is how it survived two releases. The delete now
  clears the reference on the history first, which is what deleting a Relay already does, so the log rows
  survive and keep naming the rule that matched them - the name is denormalised onto each row, the id was
  only ever a pointer. Fixes #31.

### Security

- **The dashboard's password hash is now encrypted at rest.** `webui.password_hash` was written to the SQL
  `config` table with the encryption flag off, unlike every other secret in that table, so anyone who could
  read the table (a backup, a dump, an arbitrary-read bug) held a bcrypt hash to crack offline. New writes
  go through `SecureConfig`, and the service rewrites an existing plaintext row on startup - same hash, now
  sealed - so upgraded hosts end up where fresh installs start. Login is unaffected either way.
- **A vulnerable transitive .NET dependency was floored.** `System.Security.Cryptography.Pkcs` resolved to
  10.0.0 through MimeKit and Microsoft.Data.SqlClient (GHSA-846f-f764-76mx, high; GHSA-2fmv-rv2c-59x7,
  moderate). Dependabot cannot see transitives without a committed lockfile, so it never became an alert;
  a direct pin to 10.0.13 in `Directory.Build.props` fixes it and makes the floor visible. `SECURITY.md`
  documents how to audit the whole graph (`dotnet list package --vulnerable --include-transitive`).
- **Log forging in the audit writer.** When an audit write failed, the caller-supplied `category` and `event`
  strings were interpolated into the warning verbatim, so a value containing CR/LF could inject extra lines
  into the log file. They are now stripped first (CWE-117).
- **Header injection in the Mailgun relay.** Recipients are written into the outbound multipart body as
  `to` fields. An envelope address containing CR/LF/NUL could therefore append arbitrary form fields to the
  request Mailgun receives; such addresses are now rejected before the request is built.
- **Cross-site-scripting alert in the Mailgun relay resolved.** Same code path as the item above - the
  guard is what CodeQL's `cs/web/xss` data flow wanted; covered by tests.
- **Workflow tokens are scoped.** `build.yml`, `docker.yml`, `scripts.yml`, and `installers.yml` ran with
  the repository's default (write) `GITHUB_TOKEN` permissions. They now declare `contents: read`, so a
  compromised action or script in one of those workflows can't push to the repo (9 CodeQL alerts).
- **Migration SQL identifiers are validated.** `DatabaseMigrator` interpolated table names straight into
  `SET IDENTITY_INSERT` / `setval` / `DBCC CHECKIDENT` statements. The values were literals, but they now
  pass `ProviderBootstrap.SafeIdentifier` like every other interpolated identifier, so a future caller
  can't widen them.
- **Dashboard dependencies updated.** `react-router-dom` 7.17 → 7.18.3 (RSC-mode CSRF bypass, open
  redirect via `\` in `<Link>`/`useNavigate`, arbitrary constructor injection through serialized
  router state, route-matching DoS), plus transitive floors for `postcss`, `source-map-js`, `browserslist`
  and `baseline-browser-mapping`. `npm audit` is clean in `src/Dispatch.UI`.

### Added

- **Emailit is now a relay provider.** Structured JSON sends to their v2 Emails API with a Bearer key, which
  fits alongside Resend, SMTP2GO and Maileroo - the shape theirs most resembles. To/Cc/Bcc are split from the
  envelope so a blind copy stays out of the visible headers, the From display name and any Reply-To are carried
  through rather than flattened, and attachments go base64-encoded inline. Each send also carries the message's
  spool id as an `Idempotency-Key`, which closes the awkward gap for a relay: when a response is lost the worker
  retries the same message, and Emailit replays the stored success instead of delivering it twice (a failed send
  releases the key, so retrying after a fix still sends). Their 429/500/503 responses land on Dispatch's
  transient retry path, every other 4xx is permanent. Closes #26.
### Changed

- **The dashboard password policy is now 8 characters plus a blacklist, with no character-class rule.**
  It was 12 characters *and* upper/lower/digit. The composition rule was the part worth dropping: once
  there is a length floor and a common-password check, forcing one of each class just moves people from
  `correctbatteryhorse` to `Correct1battery` - same guessability, more friction, on the credential you
  type every day. Length is a proxy for entropy, and what was actually protecting this account all along
  is elsewhere: bcrypt cost 12, the hash now encrypted at rest (§17.5), the private-ranges-by-default
  source allow-list, and the login throttle at 10 failures per 5 minutes per IP, which caps online guessing
  near 2,900 attempts a day regardless of the floor. The common-password and common-token checks were
  promoted to load-bearing and are pinned by tests at exactly 8 characters (`letmein1`, `qwerty12`,
  `welcome8` are all rejected), because that is the case where a lower floor could have quietly loosened
  something. `reset-admin-password` and the first-run wizard enforce the same function, so they moved with
  it.

- Dependabot no longer proposes EF Core majors (`Microsoft.EntityFrameworkCore*`,
  `Npgsql.EntityFrameworkCore.PostgreSQL` ≥ 10): the MySQL provider is still built against EF Core 9 and
  the bump fails at runtime with a `MissingMethodException`, not just in CI.

### Removed

- **macOS packaging is out of the release until it works on real hardware.** #48 shipped
  `dispatch-<ver>-macos.tar.gz`, a `.pkg`, a `launchd` installer, an update applier and a macOS CI job, and
  labelled the platform supported. It was not: on an actual Apple Silicon Mac the daemon is killed at exec by
  Gatekeeper, because the installed payload inherits the download's `com.apple.provenance` attribute - and
  that attribute cannot be removed by any user, including root, so it also cannot be tested by a CI runner,
  which never downloads anything. Four separate bugs of that kind reached a human rather than CI, which is the
  definition of a verification gap rather than a coding one. The whole pipeline - `installer/macos/`, the two
  LaunchDaemon property lists, the applier, the `.app`/`.pkg` builders, the runner smokes, `docs/macos.md` -
  now lives on the `macos/support` branch, where the remaining work is recorded honestly. The .NET runtime
  itself still runs fine on macOS from source; what is withdrawn is the claim and the artifacts. This returns
  when there is an Apple Developer ID to sign and notarize with, because without one the failure is silent,
  platform-side, and invisible to everything we can automate.

## [0.7.2] - 2026-08-16

### Added

- **`reset-admin-password` is now documented** (`docs/database.md`'s sibling, `docs/SPEC.md` §17.3, and a
  new "Forgot the admin password?" section on the docs site). Run locally with root/Administrator (or
  `sudo -u dispatch`), it resets the dashboard password without needing the old one - the only recovery
  path, by design, since Dispatch has no outbound email of its own to send a reset link from.
- The CLI now writes an audit entry (actor `local-cli`) so a local reset shows up in **System Logs**,
  distinguishable from a password changed in the dashboard.

### Fixed

- **A password change - from the dashboard or the CLI - did not actually invalidate other sessions.**
  Both paths bumped a session-epoch value meant to sign out every other logged-in session, but the check
  that enforces it read from an in-memory cache that only refreshes within the process that itself made
  the change. A `reset-admin-password` run (a separate process from the running service, by definition)
  could never reach that cache, so an old - possibly compromised - session stayed valid until its normal
  timeout regardless. The check now reads the session epoch live, so any password change invalidates
  every other session immediately, no matter which process made it.

## [0.7.0] - 2026-07-21

The database release. Dispatch now runs on **four database engines behind one data layer**, and the
default is a file, not a server.

### Added

- **Bundled SQLite is the default backend.** A fresh install writes to a single database file beside the
  service - there is no database server to install, back up, patch, or explain. No installer on any
  platform installs a database server any more.
- **Bring your own server engine.** Point Dispatch at a **PostgreSQL**, **MariaDB/MySQL**, or **Microsoft
  SQL Server** you already run, selected by the connection string (with an explicit `Database:Provider`
  when a connection string is ambiguous). See [docs/database.md](docs/database.md).
- **`migrate-database` command** - copy a complete database from any engine to any other, in either
  direction, preserving primary keys and verifying row counts. This is how a PostgreSQL install moves onto
  bundled SQLite, or vice-versa when you outgrow the file.
- **In-place adoption of pre-0.7 databases.** Starting the 0.7 service against an existing 0.5 database
  (which tracked its schema in a `schema_version` table) upgrades it to the EF Core migration history
  automatically, without a dump and reload.

### Changed

- **The schema is now managed by EF Core migrations**, generated per engine from a single model, replacing
  the previous hand-rolled Dapper/SQL layer. Case sensitivity, UTC timestamp handling, and identity-insert
  behaviour are normalised across engines so the choice of database does not change what users see.
- **The Windows installer no longer bundles or installs PostgreSQL.** `DispatchSetup.exe` now installs only
  the self-contained service (defaulting to SQLite), which removes the slowest, most failure-prone step of
  the old install and leaves nothing behind on uninstall.

### Fixed

- Timestamps read back from every engine now carry `DateTimeKind.Utc`, so an API response serialises the
  same instant identically regardless of backend (previously only PostgreSQL round-tripped the UTC kind).
- The database context factory is pooled through the production path, not only in tests.

### Performance

- **Keyset (cursor) pagination for the Message Log** - page N costs the same as page 1 on every engine
  (an index seek, not a scan).
- **The dashboard list now joins its deduplicated id set** instead of filtering with `id IN (subquery)`.
  The two return identical rows, but the JOIN is flat across the page offset on every engine, where the
  old form degraded into a per-row dependent subquery on MySQL/MariaDB (seconds, then a timeout, at deep
  offsets). Full cross-engine scale numbers are in [docs/database.md](docs/database.md).

### Upgrade notes

- **From 0.5 (any backend):** install 0.7 over the top and start it once. If you are staying on your
  existing PostgreSQL, the service adopts the schema in place. If you want to move onto bundled SQLite, run
  `migrate-database` first, then repoint the connection string. The Linux `install.sh` preserves your
  existing `appsettings.json` on upgrade.
- **The encryption key is not in the database.** Encrypted settings are copied as ciphertext; if you move
  to a different host, carry the `DISPATCH_KEY_DIR` directory across too, or encrypted settings become
  unreadable.

## [0.5.0] - 2026-07-10

First public release line. See the
[0.5.0 release notes](https://github.com/CinderHillsDev/dispatch-platform/releases/tag/v0.5.0).

[0.7.0]: https://github.com/CinderHillsDev/dispatch-platform/releases/tag/v0.7.0
[0.5.0]: https://github.com/CinderHillsDev/dispatch-platform/releases/tag/v0.5.0
