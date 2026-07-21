# Changelog

All notable changes to Dispatch SMTP Relay are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Download any release from the [Releases page](https://github.com/CinderHillsDev/dispatch-platform/releases).

> **A note on 0.6.** There is no public 0.6 release. `0.6` was an internal development line used while the
> database layer was rebuilt; it was never tagged or published. The public sequence goes **0.5 → 0.7**. A
> 0.5 install upgrades directly to 0.7 - see the upgrade notes below.

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
