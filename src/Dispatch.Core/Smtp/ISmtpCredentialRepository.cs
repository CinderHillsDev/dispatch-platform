namespace Dispatch.Core.Smtp;

/// <summary>A configured SMTP sender credential (spec §5.3) — never carries the password.</summary>
public sealed record SmtpCredential(int Id, string Username, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Manages the SMTP AUTH allow-list stored in <c>config_smtp_credentials</c> (bcrypt-hashed).</summary>
public interface ISmtpCredentialRepository
{
    /// <summary>Verifies a username/password against the allow-list. Constant-time even when the user is unknown.</summary>
    Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default);

    Task<IReadOnlyList<SmtpCredential>> ListAsync(CancellationToken ct = default);

    /// <summary>Adds or updates a credential (username is unique).</summary>
    Task AddAsync(string username, string password, CancellationToken ct = default);

    Task<bool> DeleteAsync(string username, CancellationToken ct = default);
}
