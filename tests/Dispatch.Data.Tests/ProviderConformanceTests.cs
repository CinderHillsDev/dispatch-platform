using Dispatch.Data;
using Dispatch.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Tests;

/// <summary>
/// The contract every registered database provider must satisfy. These need no database and run everywhere,
/// so a contributor adding an engine finds out immediately whether their provider is wired correctly -
/// rather than from a confusing failure inside an integration test that needs a server they may not have.
///
/// The point of the capability flags is that behaviour which varies between engines is DECLARED. These
/// tests are what stop a declaration drifting from reality: a provider claiming filtered-index support and
/// then returning null from IndexFilter would pass every functional test and quietly lose an invariant.
/// </summary>
public class ProviderConformanceTests
{
    public static TheoryData<DatabaseProvider> Providers()
    {
        var data = new TheoryData<DatabaseProvider>();
        foreach (var p in DatabaseProviders.All) data.Add(p.Id);
        return data;
    }

    [Fact]
    public void Every_enum_value_has_a_registered_provider()
    {
        // The enum and the registry must not drift: DatabaseProviders.Get throws for a missing one, which
        // would otherwise surface only when someone configured that engine.
        foreach (var id in Enum.GetValues<DatabaseProvider>())
            Assert.NotNull(DatabaseProviders.Get(id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Provider_declares_the_basics(DatabaseProvider id)
    {
        var p = DatabaseProviders.Get(id);

        Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(p.MigrationsAssembly));
        Assert.False(string.IsNullOrWhiteSpace(p.UtcNowSql));
        Assert.NotEmpty(p.Aliases);

        // Every engine stores UTC. CURRENT_TIMESTAMP is local time on SQL Server and MySQL/MariaDB, so a
        // provider using it must be one of the engines where that actually means UTC.
        if (p.UtcNowSql.Contains("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            Assert.True(id is DatabaseProvider.Sqlite or DatabaseProvider.Postgres,
                $"{p.DisplayName} uses CURRENT_TIMESTAMP, which is LOCAL server time on this engine. " +
                "Timestamps must be UTC - use the engine's UTC-specific function.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Migrations_assembly_exists_and_contains_migrations(DatabaseProvider id)
    {
        var p = DatabaseProviders.Get(id);

        // Resolving the context proves the migrations assembly is referenced and loadable. A missing
        // reference is otherwise a runtime FileNotFoundException at first startup on that engine.
        using var db = DispatchDbContextFactory.Create(id, PlaceholderConnectionString(id)).CreateDbContext();
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Capability_flags_match_behaviour(DatabaseProvider id)
    {
        var p = DatabaseProviders.Get(id);

        // Filtered indexes: claiming support means every predicate must be answerable, and claiming none
        // means none may be. A half-implemented provider silently drops the invariant the index enforces.
        foreach (var predicate in Enum.GetValues<IndexPredicate>())
        {
            var filter = p.IndexFilter(predicate);
            if (p.Capabilities.FilteredIndexes)
                Assert.False(string.IsNullOrWhiteSpace(filter),
                    $"{p.DisplayName} declares FilteredIndexes but returns no filter for {predicate}.");
            else
                Assert.True(filter is null,
                    $"{p.DisplayName} declares no filtered-index support but returned a filter for {predicate}.");
        }

        Assert.Equal(p.Capabilities.CoveringIndexes, p.CoveringIndexAnnotation is not null);

        // Every engine is normalised to case-sensitive LIKE, so search results do not depend on which
        // database the operator happens to run.
        Assert.True(p.Capabilities.CaseSensitiveLike,
            $"{p.DisplayName} must be configured for case-sensitive LIKE to match the other engines.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Counter_upsert_is_a_single_parameterised_statement(DatabaseProvider id)
    {
        var p = DatabaseProviders.Get(id);
        var sql = p.CounterUpsertSql("delivered");

        // The contended hot path. It must be one atomic statement keyed on both parameters - a
        // read-then-write, or a statement missing a parameter, loses increments under concurrent load.
        Assert.Contains("@date", sql);
        Assert.Contains("@relayId", sql);
        Assert.Contains("delivered", sql);
        Assert.Equal(1, sql.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Count(part => !string.IsNullOrWhiteSpace(part)));
    }

    [Fact]
    public void Distinctive_keywords_are_actually_distinctive()
    {
        // A keyword claimed by two providers makes resolution ambiguous, which is the failure mode the
        // whole distinctive/shared split exists to prevent.
        foreach (var a in DatabaseProviders.All)
            foreach (var b in DatabaseProviders.All)
            {
                if (ReferenceEquals(a, b)) continue;
                var shared = a.DistinctiveKeywords.Intersect(b.DistinctiveKeywords, StringComparer.OrdinalIgnoreCase).ToList();
                Assert.True(shared.Count == 0,
                    $"{a.DisplayName} and {b.DisplayName} both claim {string.Join(", ", shared)} as distinctive.");
            }
    }

    [Fact]
    public void Aliases_are_unique_across_providers()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in DatabaseProviders.All)
            foreach (var alias in p.Aliases)
            {
                Assert.False(seen.ContainsKey(alias),
                    $"Alias '{alias}' is claimed by both {seen.GetValueOrDefault(alias)} and {p.DisplayName}.");
                seen[alias] = p.DisplayName;
            }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Explicit_provider_setting_resolves_by_every_alias(DatabaseProvider id)
    {
        var p = DatabaseProviders.Get(id);
        foreach (var alias in p.Aliases)
            Assert.Equal(id, DatabaseProviderResolver.Resolve("Server=x;Database=y", alias));
    }

    [Theory]
    [InlineData("Data Source=/var/lib/dispatch/dispatch.db", DatabaseProvider.Sqlite)]
    [InlineData("Host=db;Port=5432;Database=DispatchLog;Username=u;Password=p", DatabaseProvider.Postgres)]
    [InlineData("Server=sql;Initial Catalog=DispatchLog;User Id=sa;Password=p", DatabaseProvider.SqlServer)]
    [InlineData("Server=maria;Database=DispatchLog;Uid=root;Pwd=p", DatabaseProvider.MySql)]
    public void Unambiguous_connection_strings_resolve_without_configuration(string cs, DatabaseProvider expected) =>
        Assert.Equal(expected, DatabaseProviderResolver.Resolve(cs));

    [Fact]
    public void Ambiguous_connection_strings_are_refused_rather_than_guessed()
    {
        // Valid for BOTH SQL Server and MySQL. Guessing wrong would not fail here - it would fail much
        // later, at some query, as an opaque syntax error.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DatabaseProviderResolver.Resolve("Server=host;Database=db;User Id=sa;Password=p"));

        Assert.Contains("Database:Provider", ex.Message);
    }

    [Fact]
    public void Unrecognised_provider_names_list_the_valid_ones()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DatabaseProviderResolver.Resolve("Data Source=x.db", "oracle"));

        foreach (var p in DatabaseProviders.All)
            Assert.Contains(p.Id.ToString(), ex.Message);
    }

    /// <summary>A syntactically valid connection string per engine; never opened.</summary>
    private static string PlaceholderConnectionString(DatabaseProvider id) => id switch
    {
        DatabaseProvider.Sqlite => "Data Source=:memory:",
        DatabaseProvider.Postgres => "Host=localhost;Database=conformance",
        DatabaseProvider.SqlServer => "Server=localhost;Initial Catalog=conformance;Integrated Security=true",
        // Pomelo would AutoDetect by connecting; the provider falls back to a pinned version when it cannot.
        DatabaseProvider.MySql => "Server=localhost;Database=conformance;Uid=root",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
