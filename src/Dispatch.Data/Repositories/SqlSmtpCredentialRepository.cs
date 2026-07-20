using Dispatch.Core.Smtp;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Data.Repositories;

/// <summary>SMTP AUTH allow-list backed by <c>config_smtp_credentials</c>; passwords bcrypt-hashed (cost 12).</summary>
public sealed class SqlSmtpCredentialRepository(IDbContextFactory<DispatchDbContext> contexts) : ISmtpCredentialRepository
{
    private const int WorkFactor = 12;
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy", WorkFactor);

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var credential = await db.SmtpCredentials.SingleOrDefaultAsync(c => c.Username == username, ct);

        if (credential is null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash);   // constant-time on unknown user
            return false;
        }
        if (!BCrypt.Net.BCrypt.Verify(password, credential.PasswordHash)) return false;

        // Recorded on the way out so the credentials page shows what is actually in use.
        credential.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SmtpCredential>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SmtpCredentials.AsNoTracking()
            .OrderBy(c => c.Username)
            .Select(c => new SmtpCredential(c.Id, c.Username, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(ct);
    }

    public async Task AddAsync(string username, string password, CancellationToken ct = default)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.SmtpCredentials.SingleOrDefaultAsync(c => c.Username == username, ct);
        if (existing is null)
            db.SmtpCredentials.Add(new SmtpCredentialEntity
            {
                Username = username,
                PasswordHash = hash,
                CreatedAt = DateTime.UtcNow,
            });
        else
            existing.PasswordHash = hash;   // set-password semantics: adding an existing user rotates it

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SmtpCredentials.Where(c => c.Username == username).ExecuteDeleteAsync(ct) > 0;
    }
}
