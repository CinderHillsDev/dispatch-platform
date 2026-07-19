using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data;

/// <summary>
/// The single schema definition for every supported backend. EF Core generates provider-specific DDL from
/// this model into the four migrations assemblies (Dispatch.Data.{Postgres,Sqlite,SqlServer,MySql}), so the
/// schema cannot drift between engines — there is one model and four renderings of it.
///
/// Table and column names are pinned explicitly to the snake_case names the hand-written migrations already
/// created. That is not cosmetic: existing PostgreSQL deployments hold live data under these names, so the
/// model must describe the schema that is already on disk rather than whatever EF's default conventions
/// would produce.
///
/// Dispatch supports two deployment shapes:
///   * bundled  — SQLite, a file next to the service. The default; no database server to install.
///   * BYO      — PostgreSQL, MariaDB/MySQL, or SQL Server that the operator already runs.
/// The engine is selected from the connection string; see <see cref="SqlConnectionFactory"/>.
///
/// Timestamps are UTC everywhere. Each provider stores them natively (timestamptz / datetime2 /
/// datetime(6) / ISO-8601 TEXT); nothing in the application should depend on the storage form.
/// </summary>
public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<ConfigEntity> Config => Set<ConfigEntity>();
    public DbSet<RelayEntity> Relays => Set<RelayEntity>();
    public DbSet<RoutingRuleEntity> RoutingRules => Set<RoutingRuleEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<RelayCounterEntity> RelayCounters => Set<RelayCounterEntity>();
    public DbSet<RelayLogEntity> RelayLog => Set<RelayLogEntity>();
    public DbSet<SmtpCredentialEntity> SmtpCredentials => Set<SmtpCredentialEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ConfigEntity>(e =>
        {
            e.ToTable("config");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.Encrypted).HasColumnName("encrypted").HasDefaultValue(false);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<RelayEntity>(e =>
        {
            e.ToTable("relays");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(64).IsRequired();
            e.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false);
            e.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
            e.Property(x => x.MaxConcurrency).HasColumnName("max_concurrency").HasDefaultValue(4);
            e.Property(x => x.MaxMessageBytes).HasColumnName("max_message_bytes").HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.Name).IsUnique();

            // At most one default relay, enforced by a unique index over only the rows where is_default is
            // true. The filter is raw SQL and every engine spells it differently — and MySQL/MariaDB has no
            // filtered indexes at all, so there the index is omitted and the invariant is upheld by
            // SqlRelayRepository (which already clears the previous default inside a transaction).
            var defaultFilter = DefaultRelayIndexFilter();
            if (defaultFilter is not null)
                e.HasIndex(x => x.IsDefault).IsUnique().HasFilter(defaultFilter).HasDatabaseName("IX_relays_default");
        });

        b.Entity<RoutingRuleEntity>(e =>
        {
            e.ToTable("routing_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.RecipientPattern).HasColumnName("recipient_pattern").HasMaxLength(256);
            e.Property(x => x.SenderPattern).HasColumnName("sender_pattern").HasMaxLength(256);
            e.Property(x => x.RelayId).HasColumnName("relay_id");
            e.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Priority).IsUnique();
            e.HasOne<RelayEntity>().WithMany().HasForeignKey(x => x.RelayId);
        });

        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(32).IsRequired();
            e.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(512).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.MessageCount).HasColumnName("message_count").HasDefaultValue(0L);
            e.Property(x => x.Revoked).HasColumnName("revoked").HasDefaultValue(false);
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RateLimitPerMinute).HasColumnName("rate_limit_per_minute").HasDefaultValue(100);
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(64).HasDefaultValue("send");
            // key_id is unique across ALL keys, revoked or not — a revoked key's id must never be reissued.
            // Unfiltered on purpose.
            e.HasIndex(x => x.KeyId).IsUnique().HasDatabaseName("UQ_api_keys_key_id");

            // Separate, non-unique lookup path for authenticating a presented key. Partial where supported,
            // so revoked keys — which accumulate and are never authenticated — stay out of it entirely.
            var liveKeyFilter = LiveApiKeyIndexFilter();
            if (liveKeyFilter is not null)
                e.HasIndex(x => x.KeyId).HasFilter(liveKeyFilter).HasDatabaseName("IX_api_keys_key_id");
        });

        b.Entity<RelayCounterEntity>(e =>
        {
            e.ToTable("relay_counters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            // A calendar date, not an instant. Stored as a date column where the engine has one.
            e.Property(x => x.Date).HasColumnName("date").HasColumnType("date");
            // Deliberately NOT a foreign key: relay_id 0 is the "no specific relay" bucket for
            // connection-level events (denials counted before routing). Migration 0007 dropped the FK for
            // exactly this reason — reinstating it here would silently drop denials from /stats again.
            e.Property(x => x.RelayId).HasColumnName("relay_id");
            e.Property(x => x.Received).HasColumnName("received").HasDefaultValue(0L);
            e.Property(x => x.Delivered).HasColumnName("delivered").HasDefaultValue(0L);
            e.Property(x => x.Failed).HasColumnName("failed").HasDefaultValue(0L);
            e.Property(x => x.Retried).HasColumnName("retried").HasDefaultValue(0L);
            e.Property(x => x.Denied).HasColumnName("denied").HasDefaultValue(0L);
            // The upsert target: SqlCounterRepository increments via a single atomic statement keyed on this.
            e.HasIndex(x => new { x.Date, x.RelayId }).IsUnique().HasDatabaseName("UQ_relay_counters");
            e.HasIndex(x => x.Date).HasDatabaseName("IX_relay_counters_date");
        });

        b.Entity<RelayLogEntity>(e =>
        {
            e.ToTable("relay_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.LoggedAt).HasColumnName("logged_at");
            e.Property(x => x.SpoolId).HasColumnName("spool_id").HasMaxLength(64).IsRequired();
            e.Property(x => x.Event).HasColumnName("event").HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            e.Property(x => x.RetryAttempt).HasColumnName("retry_attempt").HasDefaultValue(0);
            e.Property(x => x.FromAddress).HasColumnName("from_address").HasMaxLength(512).IsRequired();
            e.Property(x => x.FromDomain).HasColumnName("from_domain").HasMaxLength(255).IsRequired();
            // A JSON array of recipients, stored as text on every engine. Kept as a string rather than a
            // provider JSON type so the column behaves identically across all four.
            e.Property(x => x.ToAddresses).HasColumnName("to_addresses").IsRequired();
            e.Property(x => x.ToDomain).HasColumnName("to_domain").HasMaxLength(255).IsRequired();
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(998).IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes").HasDefaultValue(0);
            e.Property(x => x.RelayId).HasColumnName("relay_id");
            e.Property(x => x.RelayName).HasColumnName("relay_name").HasMaxLength(128);
            e.Property(x => x.RoutingRuleId).HasColumnName("routing_rule_id");
            e.Property(x => x.RoutingRuleName).HasColumnName("routing_rule_name").HasMaxLength(128);
            e.Property(x => x.RoutingMatched).HasColumnName("routing_matched").HasDefaultValue(false);
            e.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(64);
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(256);
            e.Property(x => x.ProviderResponse).HasColumnName("provider_response");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.IngestSource).HasColumnName("ingest_source").HasMaxLength(16).HasDefaultValue("SMTP");
            e.Property(x => x.SourceIp).HasColumnName("source_ip").HasMaxLength(64);
            e.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
            e.Property(x => x.ApiKeyName).HasColumnName("api_key_name").HasMaxLength(256);
            e.Property(x => x.Tags).HasColumnName("tags");
            e.Property(x => x.XMailer).HasColumnName("x_mailer").HasMaxLength(256);
            e.Property(x => x.AttachmentCount).HasColumnName("attachment_count").HasDefaultValue(0);

            e.HasOne<RelayEntity>().WithMany().HasForeignKey(x => x.RelayId);
            e.HasOne<RoutingRuleEntity>().WithMany().HasForeignKey(x => x.RoutingRuleId);
            e.HasOne<ApiKeyEntity>().WithMany().HasForeignKey(x => x.ApiKeyId);

            // Access paths, carried over from migrations 0001/0002/0005 — each was added because the query
            // it serves was scanning the table. The Postgres originals attach INCLUDE(...) payloads to make
            // some of these covering; EF has no cross-provider way to express that, so the covering payloads
            // are reapplied per-provider in the Postgres and SqlServer migrations.
            e.HasIndex(x => new { x.Status, x.LoggedAt }).HasDatabaseName("IX_relay_log_status_date");
            e.HasIndex(x => new { x.FromDomain, x.LoggedAt }).HasDatabaseName("IX_relay_log_from_domain");
            e.HasIndex(x => new { x.ToDomain, x.LoggedAt }).HasDatabaseName("IX_relay_log_to_domain");
            e.HasIndex(x => new { x.IngestSource, x.LoggedAt }).HasDatabaseName("IX_relay_log_source");
            e.HasIndex(x => x.LoggedAt).HasDatabaseName("IX_relay_log_purge");
            e.HasIndex(x => new { x.RelayId, x.LoggedAt }).HasDatabaseName("IX_relay_log_relay");
            e.HasIndex(x => new { x.RoutingRuleId, x.LoggedAt }).HasDatabaseName("IX_relay_log_rule");
            e.HasIndex(x => new { x.SpoolId, x.LoggedAt, x.Id }).HasDatabaseName("IX_relay_log_spool_id");

            // Per-API-key message list. The vast majority of rows are SMTP ingest with a NULL api_key_id;
            // filtering them out keeps this index small where the engine supports it.
            var apiKeyRows = ApiKeyLogIndexFilter();
            var apiKeyIndex = e.HasIndex(x => new { x.ApiKeyId, x.LoggedAt }).HasDatabaseName("IX_relay_log_api_key");
            if (apiKeyRows is not null) apiKeyIndex.HasFilter(apiKeyRows);
        });

        b.Entity<SmtpCredentialEntity>(e =>
        {
            e.ToTable("config_smtp_credentials");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Username).HasColumnName("username").HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(512).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.LoggedAt).HasColumnName("logged_at");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(32).IsRequired();
            e.Property(x => x.Event).HasColumnName("event").HasMaxLength(128).IsRequired();
            e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(16).HasDefaultValue("Info");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(128);
            e.Property(x => x.SourceIp).HasColumnName("source_ip").HasMaxLength(64);
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => new { x.LoggedAt, x.Id }).HasDatabaseName("IX_audit_log_at");
            e.HasIndex(x => new { x.Kind, x.LoggedAt }).HasDatabaseName("IX_audit_log_kind");
        });
    }

    // ---- Provider-conditional index filters -------------------------------------------------------
    //
    // Filtered ("partial") indexes are raw SQL and each engine spells the predicate differently — Postgres
    // and SQLite take a bare boolean column, SQL Server requires an explicit comparison and bracket-quotes
    // identifiers. MySQL and MariaDB have no filtered indexes at all, so these return null there and the
    // index is either created unfiltered or omitted, as noted at each call site.
    //
    // These read Database.ProviderName rather than the IsNpgsql()/IsSqlServer() helpers so the context does
    // not need a compile-time reference to every provider package.

    private string? DefaultRelayIndexFilter() => Provider switch
    {
        DbProvider.Postgres or DbProvider.Sqlite => "is_default",
        DbProvider.SqlServer => "[is_default] = 1",
        _ => null,     // MySQL/MariaDB: enforced in SqlRelayRepository instead
    };

    private string? LiveApiKeyIndexFilter() => Provider switch
    {
        DbProvider.Postgres or DbProvider.Sqlite => "NOT revoked",
        DbProvider.SqlServer => "[revoked] = 0",
        _ => null,
    };

    private string? ApiKeyLogIndexFilter() => Provider switch
    {
        DbProvider.Postgres or DbProvider.Sqlite => "api_key_id IS NOT NULL",
        DbProvider.SqlServer => "[api_key_id] IS NOT NULL",
        _ => null,
    };

    private DbProvider Provider => Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => DbProvider.Postgres,
        "Microsoft.EntityFrameworkCore.Sqlite" => DbProvider.Sqlite,
        "Microsoft.EntityFrameworkCore.SqlServer" => DbProvider.SqlServer,
        "Pomelo.EntityFrameworkCore.MySql" => DbProvider.MySql,
        var other => throw new InvalidOperationException($"Unsupported EF provider '{other}'."),
    };

    private enum DbProvider { Postgres, Sqlite, SqlServer, MySql }
}
