using System.Data.Common;

namespace Dispatch.Data.Dialects;

/// <summary>
/// SUPERSEDED by <see cref="Dispatch.Data.Providers.IDatabaseProvider"/>, which is the provider contract
/// for the multi-engine framework. This interface survives only because the Dapper repositories have not
/// been ported to EF yet and still reach for it; it covers PostgreSQL and SQLite alone, which is why those
/// are the only engines the repositories currently work on. Porting the repositories deletes this file.
/// Do not add members here - add them to IDatabaseProvider, or better, avoid needing them.
///
/// The database-engine-specific surface. Everything Dispatch does in SQL is deliberately written in the
/// portable subset both engines share (<c>CAST(x AS bigint)</c>, <c>CURRENT_TIMESTAMP</c>, <c>CURRENT_DATE</c>,
/// <c>ON CONFLICT ... DO UPDATE</c>, <c>RETURNING</c>, window functions, partial indexes, <c>IN @list</c>) -
/// so this interface stays small on purpose. It covers only the four things that genuinely have no common
/// form: interval arithmetic, date formatting, on-disk size introspection, and space reclamation, plus
/// connection/bootstrap concerns.
///
/// If you find yourself wanting to add a member here, first check whether the portable subset can express
/// it - every member added is a query that has to be verified twice.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Stable provider id, used for migration-folder selection and logging: "Postgres" | "Sqlite".</summary>
    string Name { get; }

    /// <summary>Creates an unopened connection. Callers own disposal.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Applies per-connection session state immediately after opening. No-op on Postgres; SQLite needs
    /// <c>busy_timeout</c> and <c>foreign_keys</c> set on every connection (neither persists in the file).
    /// </summary>
    Task OnConnectionOpenedAsync(DbConnection cn, CancellationToken ct = default);

    /// <summary>
    /// Converts a calendar date into the parameter value the engine compares correctly against a date column.
    /// Postgres binds a real <c>date</c>; SQLite stores dates as ISO TEXT, where binding a DateTime would
    /// serialise as "yyyy-MM-dd 00:00:00" and sort AFTER the bare "yyyy-MM-dd" rows - silently excluding the
    /// boundary day from range queries. Always route date parameters through this.
    /// </summary>
    object DateParam(DateTime date);

    /// <summary>
    /// A boolean SQL expression that is true when <paramref name="column"/> is older than the value bound to
    /// <paramref name="daysParam"/> (an int parameter name including its leading @).
    /// </summary>
    string OlderThanDays(string column, string daysParam);

    /// <summary>A SQL expression rendering a date column as an ISO <c>YYYY-MM-DD</c> string.</summary>
    string FormatDate(string column);

    /// <summary>Total on-disk bytes for the whole database, including indexes.</summary>
    Task<long> GetDatabaseSizeBytesAsync(DbConnection cn, CancellationToken ct = default);

    /// <summary>
    /// Total on-disk bytes attributable to one table, including its indexes. Returns 0 when the table does
    /// not exist, or when the engine cannot attribute size per table (SQLite without the dbstat module).
    /// </summary>
    Task<long> GetTableSizeBytesAsync(DbConnection cn, string table, CancellationToken ct = default);

    /// <summary>
    /// Reclaims space left by deleted rows so the on-disk size actually shrinks and the size-pressure
    /// trigger in PurgeWorker can clear. Both engines need an explicit step; neither shrinks on DELETE.
    /// </summary>
    Task ReclaimSpaceAsync(DbConnection cn, IReadOnlyList<string> tables, CancellationToken ct = default);

    /// <summary>
    /// Creates the database if it does not exist, and waits until it accepts connections. For Postgres this
    /// connects to the maintenance database and issues CREATE DATABASE; for SQLite it just ensures the
    /// containing directory exists (the file itself is created on first open).
    /// </summary>
    Task EnsureDatabaseAsync(string connectionString, CancellationToken ct = default);
}
