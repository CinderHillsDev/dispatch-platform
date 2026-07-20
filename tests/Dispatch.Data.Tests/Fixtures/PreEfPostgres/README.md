# Pre-EF PostgreSQL schema (Dispatch 0.6 and earlier)

The hand-written migrations Dispatch shipped before 0.7 moved the schema to EF Core. They are kept here,
in the test project rather than the product, for one purpose: constructing a genuine pre-0.7 database so
the upgrade path can be tested against the real thing rather than an approximation of it.

A database built by these scripts has a `schema_version` table and no `__EFMigrationsHistory`. That is
what makes it a useful fixture - and it is exactly the shape that broke the first version of the 0.7
migrator, which assumed every source was already an EF schema.

Do not edit these. They are a historical artifact; the schema is now defined by DispatchDbContext.
