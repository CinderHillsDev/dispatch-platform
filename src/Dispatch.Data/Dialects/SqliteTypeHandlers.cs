using System.Data;
using System.Globalization;
using System.Reflection;
using Dapper;

namespace Dispatch.Data.Dialects;

/// <summary>
/// Teaches Dapper how to read the two things SQLite's reader schema describes badly. Registered once,
/// process-wide, the first time a SQLite dialect is constructed.
///
/// Both problems come from the same root: SQLite has no static column typing, so
/// <c>DbDataReader.GetFieldType</c> reports the storage class of the *currently loaded row*. Dapper builds
/// its materializer from the reader schema immediately after ExecuteReader — before any row is read — so
/// for a record type it matches a constructor against types that may be wrong or unknowable:
///
///   1. Timestamps are stored as ISO-8601 TEXT, so a DateTime constructor parameter is matched against
///      System.String and no constructor matches.
///   2. On an EMPTY result set, expression columns (aggregates, CASTs) have no storage class at all and are
///      reported as System.Byte[]. Plain table columns keep their declared type, so this only bites queries
///      that select computed values and can legitimately return no rows — the per-relay counter reports.
///
/// Neither is a Dispatch bug and neither shows up on Postgres, where the reader schema is static and
/// correct. They are the price of the engine being dynamically typed, and they surface as an exception at
/// materialization rather than as wrong data, which is the failure mode to prefer.
/// </summary>
internal static class SqliteTypeHandlers
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            SqlMapper.AddTypeHandler(new DateTimeHandler());
            SqlMapper.AddTypeHandler(new NullableDateTimeHandler());
            RegisterRecordConstructors();
            _registered = true;
        }
    }

    /// <summary>
    /// Points Dapper at the single constructor of every model record it materialises, so it stops trying to
    /// choose one by matching the reader's reported column types.
    ///
    /// That matching is what fails on SQLite, in two more ways beyond the timestamp handlers above:
    ///   * every integer is reported as Int64, so an `int` parameter (SmtpCredential.Id,
    ///     MessageLogAttempt.RetryAttempt) never matches;
    ///   * on an empty result set, computed columns are reported as Byte[] (see the class comment).
    /// Both are schema-level problems, so no type handler can reach them — but neither is a real ambiguity:
    /// these records have exactly one constructor, so there is nothing to choose between. Naming it directly
    /// is what Dapper's [ExplicitConstructor] attribute does; doing it here keeps the Dapper dependency out
    /// of Dispatch.Core. Per-value conversion (Int64 to Int32, TEXT to DateTime) still happens at read time,
    /// where SQLite reports types correctly.
    ///
    /// The set is discovered by scanning rather than listed by hand: a missed type would not fail the build,
    /// it would throw at runtime on whichever query happens to return that shape.
    /// </summary>
    private static void RegisterRecordConstructors()
    {
        var core = typeof(Dispatch.Core.Counters.CounterTotals).Assembly;
        foreach (var type in core.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;

            var ctors = type.GetConstructors();
            if (ctors.Length != 1 || ctors[0].GetParameters().Length == 0) continue;

            SqlMapper.SetTypeMap(type, new SingleConstructorTypeMap(type, ctors[0]));
        }
    }

    /// <summary>
    /// A DefaultTypeMap that answers "which constructor?" with the type's only constructor instead of
    /// inferring one from reader column types.
    /// </summary>
    private sealed class SingleConstructorTypeMap(Type type, ConstructorInfo ctor)
        : SqlMapper.ITypeMap
    {
        private readonly DefaultTypeMap inner = new(type);

        public ConstructorInfo FindConstructor(string[] names, Type[] types) => ctor;
        public ConstructorInfo FindExplicitConstructor() => ctor;

        public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName) =>
            inner.GetConstructorParameter(constructor, columnName);

        public SqlMapper.IMemberMap? GetMember(string columnName) => inner.GetMember(columnName);
    }

    /// <summary>
    /// The formats SQLite timestamps can arrive in: what Microsoft.Data.Sqlite writes for a DateTime
    /// parameter (7 fractional digits), and what CURRENT_TIMESTAMP / datetime() emit (whole seconds).
    /// Parsed as UTC to match how they are written — everything Dispatch stores is UTC.
    /// </summary>
    internal static DateTime Parse(object value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string s => DateTime.SpecifyKind(
            DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault),
            DateTimeKind.Utc),
        long ticks => new DateTime(ticks, DateTimeKind.Utc),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to DateTime."),
    };

    private sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override DateTime Parse(object value) => SqliteTypeHandlers.Parse(value);

        // Let the provider serialise it; Microsoft.Data.Sqlite writes the canonical ISO-8601 form.
        public override void SetValue(IDbDataParameter parameter, DateTime value) => parameter.Value = value;
    }

    private sealed class NullableDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
    {
        public override DateTime? Parse(object value) =>
            value is null or DBNull ? null : SqliteTypeHandlers.Parse(value);

        public override void SetValue(IDbDataParameter parameter, DateTime? value) =>
            parameter.Value = (object?)value ?? DBNull.Value;
    }
}
