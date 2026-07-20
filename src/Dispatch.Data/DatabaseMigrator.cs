using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Copies a complete Dispatch database from one engine to another - the 0.7 path for moving an existing
/// PostgreSQL install onto the bundled SQLite backend.
///
/// This is a data migration, not a schema one. Both ends share <see cref="DispatchDbContext"/>, so the
/// target schema is created by its own provider's migrations and the copy is entity-by-entity; each
/// provider handles its own type mapping on the way in and out. That is what makes this engine-agnostic
/// rather than a PostgreSQL-to-SQLite script.
///
/// Primary keys are preserved deliberately. relay_log.id is not an implementation detail: it is half of the
/// Message Log's keyset cursor and the target of every foreign key in the schema. Renumbering rows would
/// silently break routing-rule and API-key attribution on historical mail.
///
/// Encrypted config values are copied verbatim, ciphertext and flag together, and never decrypted in
/// transit. See <see cref="SecureConfig"/> - the key is host-portable, so re-encrypting would be pointless
/// risk for no gain.
/// </summary>
public sealed class DatabaseMigrator(ILogger<DatabaseMigrator>? log = null)
{
    /// <summary>Rows per round trip. Large enough to amortise latency, small enough to bound memory on a
    /// relay_log with millions of rows.</summary>
    private const int BatchSize = 5_000;

    public sealed record Result(IReadOnlyDictionary<string, int> RowsCopied, TimeSpan Elapsed)
    {
        public int Total => RowsCopied.Values.Sum();
    }

    /// <summary>
    /// Copies every table from source to target. The target must already be migrated and must be empty -
    /// this refuses to merge into a database that holds data, because there is no sane conflict resolution
    /// for two rows that claim the same relay_log.id.
    /// </summary>
    public async Task<Result> CopyAsync(
        DatabaseProvider sourceProvider, string sourceConnection,
        DatabaseProvider targetProvider, string targetConnection,
        CancellationToken ct = default)
    {
        if (targetProvider == DatabaseProvider.SqlServer)
            throw new NotSupportedException(
                "SQL Server is not supported as a migration target: inserting explicit identity values " +
                "requires SET IDENTITY_INSERT handling that is not implemented or tested here.");

        var started = DateTime.UtcNow;
        var counts = new Dictionary<string, int>();

        var source = DispatchDbContextFactory.Create(sourceProvider, sourceConnection);
        var target = KeyPreservingContextFactory(targetProvider, targetConnection);

        // Check the source is actually a Dispatch database before touching the target. Without this, a
        // wrong connection string surfaces as a raw "relation config does not exist" partway through -
        // which is a confusing thing to read while migrating a production install.
        await using (var check = await source.CreateDbContextAsync(ct))
        {
            if (await check.Database.GetPendingMigrationsAsync(ct) is { } sourcePending && sourcePending.Any())
            {
                // A pre-0.7 database is the expected reason to land here, and it has a specific fix. The
                // copy reads the source THROUGH the EF model, so the source has to be a schema EF
                // recognises; one built by the old hand-written scripts is not, until the service has
                // started against it once and adopted it. Saying that is far more use than "not a current
                // schema" to someone midway through upgrading a production install.
                var preEf = await DatabaseInitializer.HasPreEfSchemaAsync(check, ct);
                throw new InvalidOperationException(preEf
                    ? "The source is a pre-0.7 Dispatch database (it still has a schema_version table). "
                      + "Start the 0.7 service against it once - that upgrades the schema in place, without "
                      + "touching your data - then stop it and re-run this migration."
                    : $"The source database is not a current Dispatch schema ({sourcePending.Count()} "
                      + "migration(s) not applied). Check that the connection string points at the running "
                      + "install's database.");
            }
        }

        await using (var check = await target.CreateDbContextAsync(ct))
        {
            if (await check.Database.GetPendingMigrationsAsync(ct) is { } p && p.Any())
                throw new InvalidOperationException(
                    "The target database schema is not up to date. Run the service against it once, or " +
                    "apply migrations, before migrating data into it.");

            if (await check.RelayLog.AnyAsync(ct) || await check.Relays.AnyAsync(ct) || await check.Config.AnyAsync(ct))
                throw new InvalidOperationException(
                    "The target database already contains data. Migrate into an empty database - merging " +
                    "two histories would collide on primary keys that are still referenced.");
        }

        // FK dependency order: relays and routing_rules must exist before the relay_log rows that point at
        // them, and routing_rules points at relays.
        counts["config"] = await CopyTableAsync(source, target, c => c.Config, x => x.Key, ct);
        counts["relays"] = await CopyTableAsync(source, target, c => c.Relays, x => x.Id, ct);
        counts["routing_rules"] = await CopyTableAsync(source, target, c => c.RoutingRules, x => x.Id, ct);
        counts["api_keys"] = await CopyTableAsync(source, target, c => c.ApiKeys, x => x.Id, ct);
        counts["relay_counters"] = await CopyTableAsync(source, target, c => c.RelayCounters, x => x.Id, ct);
        counts["config_smtp_credentials"] = await CopyTableAsync(source, target, c => c.SmtpCredentials, x => x.Id, ct);
        counts["audit_log"] = await CopyTableAsync(source, target, c => c.AuditLog, x => x.Id, ct);
        counts["relay_log"] = await CopyTableAsync(source, target, c => c.RelayLog, x => x.Id, ct);

        await ResetIdentitySequencesAsync(targetProvider, target, ct);
        await VerifyAsync(source, target, counts, ct);

        var result = new Result(counts, DateTime.UtcNow - started);
        log?.LogInformation("Migrated {Total} rows from {Source} to {Target} in {Elapsed}",
            result.Total, sourceProvider, targetProvider, result.Elapsed);
        return result;
    }

    /// <summary>
    /// Streams one table across in key-ordered batches. Ordering by the primary key makes the paging stable
    /// and the whole copy resumable-looking in the logs; AsNoTracking keeps the source context from
    /// accumulating every row it has ever seen.
    /// </summary>
    private async Task<int> CopyTableAsync<TEntity, TKey>(
        IDbContextFactory<DispatchDbContext> source,
        IDbContextFactory<DispatchDbContext> target,
        Func<DispatchDbContext, DbSet<TEntity>> set,
        System.Linq.Expressions.Expression<Func<TEntity, TKey>> key,
        CancellationToken ct)
        where TEntity : class
    {
        var copied = 0;
        var compiledKey = key.Compile();
        TKey? last = default;
        var first = true;

        while (true)
        {
            List<TEntity> batch;
            await using (var db = await source.CreateDbContextAsync(ct))
            {
                var q = set(db).AsNoTracking().OrderBy(key);
                // Keyset paging rather than Skip/Take: Skip re-scans everything already copied, which turns
                // a large relay_log into a quadratic crawl.
                if (!first)
                    q = (IOrderedQueryable<TEntity>)Queryable.Where(q, GreaterThan(key, last!));
                batch = await q.Take(BatchSize).ToListAsync(ct);
            }

            if (batch.Count == 0) break;

            await using (var db = await target.CreateDbContextAsync(ct))
            {
                set(db).AddRange(batch);
                await db.SaveChangesAsync(ct);
            }

            copied += batch.Count;
            last = compiledKey(batch[^1]);
            first = false;

            if (batch.Count < BatchSize) break;
            log?.LogDebug("Copied {Count} rows of {Entity}", copied, typeof(TEntity).Name);
        }

        log?.LogInformation("Copied {Count} {Entity} rows", copied, typeof(TEntity).Name);
        return copied;
    }

    private static System.Linq.Expressions.Expression<Func<TEntity, bool>> GreaterThan<TEntity, TKey>(
        System.Linq.Expressions.Expression<Func<TEntity, TKey>> key, TKey value)
    {
        var param = key.Parameters[0];
        var body = System.Linq.Expressions.Expression.GreaterThan(
            key.Body, System.Linq.Expressions.Expression.Constant(value, typeof(TKey)));
        return System.Linq.Expressions.Expression.Lambda<Func<TEntity, bool>>(body, param);
    }

    /// <summary>
    /// After inserting explicit key values, the engine's key generator has to be told where the data now
    /// ends - otherwise the next insert reuses an id that is already taken.
    ///
    /// SQLite (AUTOINCREMENT) and MySQL (AUTO_INCREMENT) advance their counters as a side effect of an
    /// explicit insert. PostgreSQL identity sequences do not, so they are advanced here.
    /// </summary>
    private async Task ResetIdentitySequencesAsync(
        DatabaseProvider provider, IDbContextFactory<DispatchDbContext> target, CancellationToken ct)
    {
        if (provider != DatabaseProvider.Postgres) return;

        await using var db = await target.CreateDbContextAsync(ct);
        foreach (var table in new[] { "relays", "routing_rules", "api_keys", "relay_counters", "relay_log", "audit_log", "config_smtp_credentials" })
        {
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), " +
                $"COALESCE((SELECT MAX(id) FROM {table}), 1), true);", ct);
        }
    }

    /// <summary>
    /// Re-counts both sides. The copy is not a transaction spanning two engines, so this is what turns
    /// "it appeared to work" into evidence - a short read that would catch a batch silently dropped.
    /// </summary>
    private static async Task VerifyAsync(
        IDbContextFactory<DispatchDbContext> source, IDbContextFactory<DispatchDbContext> target,
        IReadOnlyDictionary<string, int> counts, CancellationToken ct)
    {
        await using var src = await source.CreateDbContextAsync(ct);
        await using var dst = await target.CreateDbContextAsync(ct);

        var mismatches = new List<string>();
        async Task Check(string name, IQueryable<object> a, IQueryable<object> b)
        {
            var (from, to) = (await a.CountAsync(ct), await b.CountAsync(ct));
            if (from != to) mismatches.Add($"{name}: source {from}, target {to}");
        }

        await Check("config", src.Config, dst.Config);
        await Check("relays", src.Relays, dst.Relays);
        await Check("routing_rules", src.RoutingRules, dst.RoutingRules);
        await Check("api_keys", src.ApiKeys, dst.ApiKeys);
        await Check("relay_counters", src.RelayCounters, dst.RelayCounters);
        await Check("config_smtp_credentials", src.SmtpCredentials, dst.SmtpCredentials);
        await Check("audit_log", src.AuditLog, dst.AuditLog);
        await Check("relay_log", src.RelayLog, dst.RelayLog);

        if (mismatches.Count > 0)
            throw new InvalidOperationException(
                "Migration verification failed - row counts differ:\n  " + string.Join("\n  ", mismatches));
    }

    private static IDbContextFactory<DispatchDbContext> KeyPreservingContextFactory(
        DatabaseProvider provider, string connectionString)
    {
        var builder = new DbContextOptionsBuilder<KeyPreservingDbContext>();
        DispatchDbContextFactory.Configure(builder, provider, connectionString);
        return new Factory((DbContextOptions<KeyPreservingDbContext>)builder.Options);
    }

    private sealed class Factory(DbContextOptions<KeyPreservingDbContext> options)
        : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() => new KeyPreservingDbContext(options);
    }
}

/// <summary>
/// The model, with key generation switched off so explicit primary keys survive the copy. A distinct
/// context type rather than a flag, because EF caches one compiled model per context type - a runtime flag
/// on <see cref="DispatchDbContext"/> would leak into whichever configuration happened to be built first.
/// </summary>
internal sealed class KeyPreservingDbContext(DbContextOptions<KeyPreservingDbContext> options)
    : DispatchDbContext(ConvertOptions(options))
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<RelayEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<RoutingRuleEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<ApiKeyEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<RelayCounterEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<RelayLogEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<SmtpCredentialEntity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<AuditLogEntity>().Property(x => x.Id).ValueGeneratedNever();
    }

    // The base constructor is typed to DbContextOptions<DispatchDbContext>; the options built here carry
    // the derived type. Same underlying extensions either way.
    private static DbContextOptions<DispatchDbContext> ConvertOptions(DbContextOptions<KeyPreservingDbContext> options)
    {
        var builder = new DbContextOptionsBuilder<DispatchDbContext>();
        foreach (var extension in options.Extensions)
            ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)builder)
                .AddOrUpdateExtension(extension);
        return builder.Options;
    }
}
