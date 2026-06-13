using Dapper;
using Dispatch.Core.Smtp;

namespace Dispatch.Data.Repositories;

/// <summary>SMTP AUTH allow-list backed by <c>config_smtp_credentials</c>; passwords bcrypt-hashed (cost 12).</summary>
public sealed class SqlSmtpCredentialRepository(SqlConnectionFactory factory) : ISmtpCredentialRepository
{
    private const int WorkFactor = 12;
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy", WorkFactor);

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var hash = await cn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT password_hash FROM config_smtp_credentials WHERE username = @username", new { username }, cancellationToken: ct));

        if (hash is null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash);   // constant-time on unknown user
            return false;
        }
        if (!BCrypt.Net.BCrypt.Verify(password, hash)) return false;

        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE config_smtp_credentials SET last_used_at = SYSUTCDATETIME() WHERE username = @username",
            new { username }, cancellationToken: ct));
        return true;
    }

    public async Task<IReadOnlyList<SmtpCredential>> ListAsync(CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var rows = await cn.QueryAsync<SmtpCredential>(new CommandDefinition(
            "SELECT id AS Id, username AS Username, created_at AS CreatedAt, last_used_at AS LastUsedAt FROM config_smtp_credentials ORDER BY username",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task AddAsync(string username, string password, CancellationToken ct = default)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
        const string sql = """
            MERGE config_smtp_credentials AS t
            USING (VALUES (@username)) AS s(username) ON t.username = s.username
            WHEN MATCHED THEN UPDATE SET password_hash = @hash
            WHEN NOT MATCHED THEN INSERT (username, password_hash) VALUES (@username, @hash);
            """;
        await using var cn = await factory.OpenAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(sql, new { username, hash }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken ct = default)
    {
        await using var cn = await factory.OpenAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM config_smtp_credentials WHERE username = @username", new { username }, cancellationToken: ct));
        return n > 0;
    }
}
