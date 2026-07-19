using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Providers;

/// <summary>
/// Everything Dispatch needs to know about one database engine, in one place.
///
/// ADDING AN ENGINE: implement this interface, add a migrations assembly named in
/// <see cref="MigrationsAssembly"/>, and register the provider in <see cref="DatabaseProviders"/>. The
/// shared test suite then runs against it unchanged - that is the acceptance criterion, not a separate
/// checklist. See docs/database.md.
///
/// The interface is deliberately small. Everything Dispatch does in SQL is written in the portable subset
/// the engines share, and EF Core generates the rest; what remains here is only what genuinely has no
/// common form. If you find yourself wanting to add a member, first check whether the portable subset or a
/// LINQ query can express it - every member added is behaviour that has to be implemented and verified
/// once per engine, forever.
///
/// The engines split into two deployment shapes:
///   * bundled - SQLite. A file beside the service, no server to install. The default.
///   * BYO     - PostgreSQL, MariaDB/MySQL, SQL Server that the operator already runs.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>Stable identifier used in configuration and logs.</summary>
    DatabaseProvider Id { get; }

    /// <summary>Human-readable engine name, e.g. "PostgreSQL", "MariaDB / MySQL".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Assembly holding this engine's EF migrations. Migrations are provider-specific because the generated
    /// DDL is; the model they are generated from is shared.
    /// </summary>
    string MigrationsAssembly { get; }

    /// <summary>What this engine can and cannot do - see <see cref="ProviderCapabilities"/>.</summary>
    ProviderCapabilities Capabilities { get; }

    // ---- Identification -------------------------------------------------------------------------

    /// <summary>
    /// Connection-string keywords that identify this engine unambiguously - keywords no other supported
    /// engine accepts. Shared keywords like "Server" or "Database" must NOT appear here: they are what make
    /// SQL Server and MySQL indistinguishable, and a wrong guess there fails much later as an opaque syntax
    /// error. <see cref="DatabaseProviders.Resolve"/> refuses ambiguity rather than picking.
    /// </summary>
    IReadOnlySet<string> DistinctiveKeywords { get; }

    /// <summary>Alternate spellings accepted for the explicit Database:Provider setting, lowercase.</summary>
    IReadOnlySet<string> Aliases { get; }

    // ---- EF wiring ------------------------------------------------------------------------------

    /// <summary>Points a DbContext at this engine, including the migrations assembly.</summary>
    void Configure(DbContextOptionsBuilder builder, string connectionString);

    /// <summary>Creates a raw connection, for the few operations that bypass EF.</summary>
    DbConnection CreateConnection(string connectionString);

    // ---- Schema conventions ---------------------------------------------------------------------

    /// <summary>
    /// SQL for "now, in UTC", as a column default. NOT the same expression everywhere: CURRENT_TIMESTAMP is
    /// local server time on SQL Server and MySQL/MariaDB and UTC on PostgreSQL and SQLite. Dispatch stores
    /// UTC throughout, so the portable-looking spelling would write plausible-but-skewed timestamps on half
    /// the engines with nothing raised.
    /// </summary>
    string UtcNowSql { get; }

    /// <summary>
    /// The collation the schema is created with, or null where the engine has no collation concept.
    ///
    /// This is what makes LIKE case-sensitive on MySQL/MariaDB and SQL Server, whose default collations are
    /// case-INSENSITIVE. Declaring it on the MODEL rather than only on the database matters: a table created
    /// with an explicit CHARACTER SET does not inherit the database's collation, so relying on inheritance
    /// silently gives you case-insensitive search on some servers and not others.
    /// </summary>
    string? DefaultCollation { get; }

    /// <summary>
    /// A filtered-index predicate, in this engine's syntax, or null when the engine has no filtered
    /// indexes. Callers must handle null by creating an unfiltered index or omitting it - see the call
    /// sites in DispatchDbContext, which document which choice applies where.
    /// </summary>
    string? IndexFilter(IndexPredicate predicate);

    /// <summary>
    /// The model annotation that attaches a covering-index payload, or null where the engine has none.
    /// Returns the annotation name; the value is the property-name array.
    /// </summary>
    string? CoveringIndexAnnotation { get; }

    // ---- Bootstrap ------------------------------------------------------------------------------

    /// <summary>
    /// Creates the database if absent and waits for the server to accept connections. EF can create a
    /// database but will not wait for one that is still starting, which is exactly what a compose stack or
    /// a freshly-provisioned VM does on first boot.
    /// </summary>
    Task EnsureDatabaseAsync(string connectionString, ILogger? log, CancellationToken ct = default);

    /// <summary>
    /// Applies per-connection session state after opening. SQLite needs busy_timeout, foreign_keys and
    /// case_sensitive_like set on every connection; the server engines generally need nothing.
    /// </summary>
    Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken ct = default);

    // ---- Operations EF cannot express -----------------------------------------------------------

    /// <summary>
    /// An atomic "insert this counter row, or add one to the existing row" statement, parameterised on
    /// @date and @relayId. This MUST be a single statement: it is the contended hot path, hit by every
    /// worker thread for the same (date, relay_id), and a read-then-write would lose increments under load.
    /// MERGE, ON CONFLICT and ON DUPLICATE KEY are all spelled differently, hence provider-specific.
    /// </summary>
    string CounterUpsertSql(string column);

    /// <summary>Total on-disk bytes for the whole database, including indexes.</summary>
    Task<long> GetDatabaseSizeBytesAsync(DbContext db, CancellationToken ct = default);

    /// <summary>
    /// On-disk bytes for one table including its indexes, or 0 when the engine cannot attribute size per
    /// table. Return 0 rather than estimating - a fabricated number that looks authoritative is worse than
    /// an absent one.
    /// </summary>
    Task<long> GetTableSizeBytesAsync(DbContext db, string table, CancellationToken ct = default);

    /// <summary>
    /// Reclaims space left by deleted rows so on-disk size actually shrinks and the size-pressure trigger
    /// in PurgeWorker can clear. No engine shrinks on DELETE alone. This holds heavy locks on every engine
    /// and is a maintenance action, never a hot-path call.
    /// </summary>
    Task ReclaimSpaceAsync(DbContext db, IReadOnlyList<string> tables, CancellationToken ct = default);
}

/// <summary>The filtered indexes the schema wants; each engine spells the predicate its own way.</summary>
public enum IndexPredicate
{
    /// <summary>Rows where relays.is_default is true - enforces at most one default relay.</summary>
    DefaultRelay,
    /// <summary>Rows where api_keys.revoked is false - the authentication lookup path.</summary>
    LiveApiKey,
    /// <summary>Rows where relay_log.api_key_id is not null - the per-API-key message list.</summary>
    ApiKeyAttributedLog,
}

/// <summary>
/// What an engine supports. These exist so behaviour that varies is declared and testable rather than
/// discovered in production: the shared test suite reads these to decide which invariants to assert, so a
/// new provider that cannot do something states it here instead of silently failing a test.
/// </summary>
public sealed record ProviderCapabilities
{
    /// <summary>
    /// Partial/filtered indexes (CREATE INDEX ... WHERE). MySQL and MariaDB have none, so invariants that
    /// rely on a filtered UNIQUE index must be enforced in application code there instead.
    /// </summary>
    public required bool FilteredIndexes { get; init; }

    /// <summary>Covering-index payloads (INCLUDE). A performance property, never a correctness one.</summary>
    public required bool CoveringIndexes { get; init; }

    /// <summary>
    /// Whether LIKE is case-sensitive once Dispatch has configured the engine. Every engine is normalised
    /// to case-SENSITIVE - matching the original PostgreSQL behaviour - because search semantics changing
    /// with the operator's choice of database would be a surprising way to lose results.
    /// </summary>
    public required bool CaseSensitiveLike { get; init; }

    /// <summary>
    /// Whether explicit primary-key values can be inserted without extra ceremony. False for SQL Server,
    /// which needs SET IDENTITY_INSERT - the reason DatabaseMigrator refuses it as a target.
    /// </summary>
    public required bool PlainIdentityInsert { get; init; }

    /// <summary>
    /// Whether the engine can report per-table on-disk size. False for SQLite unless the dbstat module is
    /// compiled in, in which case the storage view degrades to whole-database size plus exact row counts.
    /// </summary>
    public required bool PerTableSizeReporting { get; init; }
}
