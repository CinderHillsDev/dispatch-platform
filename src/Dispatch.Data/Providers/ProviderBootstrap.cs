using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dispatch.Data.Providers;

/// <summary>Helpers shared by the provider implementations. Not part of the provider contract.</summary>
internal static class ProviderBootstrap
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 30;   // ~60s

    /// <summary>
    /// Opens a connection, retrying for ~60s so a database container that is still starting is tolerated
    /// rather than crash-looping the service on first boot.
    /// </summary>
    public static async Task OpenWithRetryAsync(DbConnection cn, ILogger? log, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cn.OpenAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is not OperationCanceledException)
            {
                if (attempt == 1) log?.LogInformation("Waiting for the database server to accept connections…");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>Runs a scalar query returning a long, on the context's connection.</summary>
    public static async Task<long> ScalarAsync(DbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Guards an identifier that is being interpolated into DDL rather than bound as a parameter — table
    /// names cannot be parameters in VACUUM or a size function. Every call site passes a literal from our
    /// own code, so this is a backstop against a future caller passing something else, not a sanitiser.
    /// </summary>
    public static string SafeIdentifier(string name)
    {
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"'{name}' is not a valid table identifier.", nameof(name));
        return name;
    }
}
