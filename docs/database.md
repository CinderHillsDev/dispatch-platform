# Database backends

Dispatch runs on four database engines, in two deployment shapes:

| Shape | Engine | When |
| --- | --- | --- |
| **Bundled** | SQLite | The default. A file beside the service — no database server to install, back up, patch, or explain. |
| **Bring your own** | PostgreSQL, MariaDB / MySQL, Microsoft SQL Server | The site already runs one and wants Dispatch's data in it. |

Dispatch does **not** install a database server for you. If you want a server backend, point Dispatch at
one you already run.

## Choosing a backend

The engine is chosen by the connection string:

```jsonc
// appsettings.json — bundled SQLite (the default)
"ConnectionStrings": { "DispatchLog": "Data Source=/var/lib/dispatch/dispatch.db" }

// PostgreSQL
"ConnectionStrings": { "DispatchLog": "Host=db;Database=DispatchLog;Username=dispatch;Password=…" }
```

Most connection strings identify their engine unambiguously. Two do not: `Server=…;Database=…;User Id=…`
is valid for **both** SQL Server and MySQL/MariaDB. Dispatch refuses to guess, because guessing wrong
surfaces much later as an opaque SQL syntax error rather than a clear failure at startup. Say which:

```jsonc
"ConnectionStrings": { "DispatchLog": "Server=sql;Database=DispatchLog;User Id=dispatch;Password=…" },
"Database":          { "Provider": "SqlServer" }   // or MySql, Postgres, Sqlite
```

The environment-variable form is `Database__Provider`.

## Moving between engines

`migrate-database` copies a complete database from one engine to another — this is how a PostgreSQL
install moves onto bundled SQLite:

```bash
# Stop the service first: rows written during the copy would be missed.
systemctl stop dispatch

Dispatch.Service migrate-database --to "Data Source=/var/lib/dispatch/dispatch.db"
```

It reads the source and never writes to it, so a failed or wrong run costs nothing but time — point the
connection string back and retry. Primary keys are preserved, because `relay_log.id` is half the Message
Log's pagination cursor and the target of every foreign key. Row counts are verified against the source
before it reports success.

When it finishes, update `ConnectionStrings:DispatchLog` to the new target and start the service.

> **The encryption key is not in the database.** Encrypted settings are copied as ciphertext. The key lives
> in `DISPATCH_KEY_DIR`, so migrating onto a *different host* means carrying that directory across too —
> otherwise every encrypted setting becomes unreadable.

SQL Server is not supported as a migration *target* (inserting explicit identity values needs
`SET IDENTITY_INSERT` handling that is neither implemented nor tested). It works fine as a backend.

## What differs between engines

Dispatch normalises behaviour where it can, so the choice of database does not change what users see. The
differences that remain are declared in each provider's `ProviderCapabilities` and enforced by
`ProviderConformanceTests`:

| | SQLite | PostgreSQL | SQL Server | MariaDB / MySQL |
| --- | --- | --- | --- | --- |
| Filtered (partial) indexes | ✅ | ✅ | ✅ | ❌ |
| Covering indexes (`INCLUDE`) | ❌ | ✅ | ✅ | ❌ |
| Per-table size reporting | ❌¹ | ✅ | ✅ | ✅ |
| Explicit identity insert | ✅ | ✅ | ❌² | ✅ |
| Case-sensitive `LIKE` | ✅³ | ✅ | ✅⁴ | ✅⁴ |

1. Needs the `dbstat` module, absent from most SQLite builds. The storage view falls back to
   whole-database size plus exact row counts rather than showing an invented number.
2. Needs `SET IDENTITY_INSERT`; only affects `migrate-database` as a target.
3. Via `PRAGMA case_sensitive_like`, set on every connection.
4. Via the collation chosen at database creation (`Latin1_General_BIN2`, `utf8mb4_bin`).

**Case sensitivity is deliberately normalised.** SQLite's `LIKE`, SQL Server's default collation, and
MySQL's default collation are all case-**in**sensitive; PostgreSQL's is not. Left alone, Message Log
subject search, tag matching and audit search would silently return more results on some backends than
others. Every engine is configured case-sensitive to match. Changing that should be a deliberate product
decision, not a consequence of which database an operator happens to run.

Missing covering indexes and per-table sizes are performance and reporting differences. Nothing in the
table above changes a correctness guarantee.

## Adding an engine

1. **Implement `IDatabaseProvider`** in `src/Dispatch.Data/Providers/`. The interface is small on purpose —
   it covers only what genuinely has no portable form. Before adding a member to it, check whether the
   portable SQL subset or a LINQ query can express what you need; every member is behaviour that must be
   implemented and verified once per engine, forever.

2. **Add a migrations assembly**, `src/Dispatch.Data.<Engine>/`, matching `MigrationsAssembly`. Copy an
   existing one — it is a `.csproj`, a `DesignTimeFactory`, and generated migrations. Reference it from
   `Dispatch.Service` and `Dispatch.Data.Tests`, or it will not be present at runtime.

3. **Register it** in `DatabaseProviders.All`. That is the only list; the DbContext, initializer, migrator
   and test matrix all read from it.

4. **Generate the schema:**
   ```bash
   dotnet ef migrations add InitialSchema \
     --project src/Dispatch.Data.<Engine> --startup-project src/Dispatch.Data.<Engine> \
     --context DispatchDbContext -o Migrations
   ```

5. **Run the tests.** `ProviderConformanceTests` needs no database and checks the wiring — including that
   your declared capabilities match your actual behaviour. Then run the full suite against a real server:
   ```bash
   dotnet test tests/Dispatch.Data.Tests                       # SQLite (default)
   DISPATCH_TEST_ENGINE=<engine> DISPATCH_TEST_SQL="…" dotnet test tests/Dispatch.Data.Tests
   ```

The same suite passing against your engine is the acceptance criterion. There is no separate checklist.

### Things that have bitten us

Worth reading before you assume your engine is boring:

- **`CURRENT_TIMESTAMP` is not UTC everywhere.** It is local server time on SQL Server and MySQL/MariaDB.
  Dispatch stores UTC throughout, so using it would write plausible-looking, skewed timestamps — wrong
  ordering, wrong retention, nothing raised. Use your engine's UTC-specific function in `UtcNowSql`.
- **The counter upsert must be one statement.** It is the contended hot path: every worker thread targets
  the same `(date, relay_id)` row. A read-then-write loses increments under load. `MERGE` needs `HOLDLOCK`
  on SQL Server to be atomic against a concurrent `MERGE`.
- **Declare capabilities honestly.** A provider that claims filtered-index support and then returns no
  filter would pass every functional test while silently dropping the invariant the index enforces.
  `ProviderConformanceTests` checks for exactly this.
- **Do not let `Configure` require a live connection.** Pomelo's `ServerVersion.AutoDetect` opens one,
  which breaks design-time scaffolding and any startup that happens before the database is reachable. See
  `MySqlDatabaseProvider.DetectServerVersion` for the fallback pattern.

## Migrations

Schema changes are made once, in `DispatchDbContext`, then generated per engine:

```bash
for e in Postgres Sqlite SqlServer MySql; do
  dotnet ef migrations add <Name> \
    --project src/Dispatch.Data.$e --startup-project src/Dispatch.Data.$e \
    --context DispatchDbContext -o Migrations
done
```

There is one model and four renderings of it, so the engines cannot drift apart. Migrations are applied at
startup by `DatabaseInitializer`.
