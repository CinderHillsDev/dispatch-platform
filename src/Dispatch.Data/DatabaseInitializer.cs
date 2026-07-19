using Dapper;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data;

/// <summary>
/// Ensures the target database exists and applies ordered, embedded SQL migrations once each,
/// tracking applied versions in <c>schema_version</c> (spec §6.11, §12). Idempotent and safe to
/// run on every startup.
///
/// Migrations are per-engine: the DDL dialects diverge enough (identity columns, INCLUDE indexes,
/// DROP CONSTRAINT, timestamp types) that sharing one set would mean a translation layer more fragile
/// than two readable files. Both sets carry the same version numbers and the same meaning at each
/// version, so schema_version is comparable across engines.
/// </summary>
public sealed class DatabaseInitializer(SqlConnectionFactory factory, ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await factory.Dialect.EnsureDatabaseAsync(factory.ConnectionString, ct);
        await EnsureSchemaVersionTableAsync(ct);
        await ApplyMigrationsAsync(ct);
    }

    private async Task EnsureSchemaVersionTableAsync(CancellationToken ct)
    {
        await using var cn = await factory.OpenAsync(ct);
        // Written in the portable subset so one statement serves both engines: TEXT/varchar and
        // int/INTEGER are compatible declarations, and CURRENT_TIMESTAMP exists in both.
        await cn.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version     int  NOT NULL PRIMARY KEY,
                script_name text NOT NULL,
                applied_at  text NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, cancellationToken: ct));
    }

    private async Task ApplyMigrationsAsync(CancellationToken ct)
    {
        await using var cn = await factory.OpenAsync(ct);
        var applied = (await cn.QueryAsync<int>(new CommandDefinition(
            "SELECT version FROM schema_version", cancellationToken: ct))).ToHashSet();

        foreach (var (version, name, sql) in LoadMigrations(factory.Dialect.Name))
        {
            if (applied.Contains(version))
                continue;

            await using var tx = await cn.BeginTransactionAsync(ct);
            try
            {
                await cn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
                await cn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_version (version, script_name) VALUES (@version, @name)",
                    new { version, name }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                log.LogInformation("Applied migration {Version} ({Name}) [{Engine}]", version, name, factory.Dialect.Name);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    /// <summary>
    /// Loads the embedded migrations for one engine. Resource names look like
    /// "Dispatch.Data.Migrations.Sqlite.0001_init.sql", so the engine name selects the folder.
    /// </summary>
    internal static IEnumerable<(int Version, string Name, string Sql)> LoadMigrations(string engine)
    {
        var asm = typeof(DatabaseInitializer).Assembly;
        var prefix = $".Migrations.{engine}.";
        var migrations = new List<(int, string, string)>();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            var idx = resource.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = resource[(idx + prefix.Length)..];                   // e.g. "0001_init.sql"
            var versionText = name.Split('_', 2)[0];
            if (!int.TryParse(versionText, out var version))
                continue;

            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add((version, name, reader.ReadToEnd()));
        }

        if (migrations.Count == 0)
            throw new InvalidOperationException($"No embedded migrations found for engine '{engine}'.");

        return migrations.OrderBy(m => m.Item1);
    }
}
