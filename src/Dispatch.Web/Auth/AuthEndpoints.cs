using System.Security.Claims;
using Dispatch.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Auth;

/// <summary>
/// Web-UI authentication (spec §17.1–§17.3). Auth is mandatory: a single admin password (bcrypt-hashed in
/// the SQL config table) is set at install time, or via a one-time first-run setup screen if none exists.
/// Login establishes a cookie session.
/// </summary>
public static class AuthEndpoints
{
    public const string PasswordHashKey = "webui.password_hash";

    public static void MapAuth(this RouteGroupBuilder group)
    {
        group.MapGet("/auth/status", async (HttpContext ctx, IConfigRepository config) =>
        {
            var hasPassword = !string.IsNullOrEmpty(await config.GetAsync(PasswordHashKey, ctx.RequestAborted));
            return Results.Ok(new
            {
                authRequired = true,                       // always on
                needsSetup = !hasPassword,                 // first-run: no admin password yet
                authenticated = ctx.User.Identity?.IsAuthenticated ?? false,
            });
        });

        group.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IConfigRepository config, LoginThrottle throttle) =>
        {
            // Per-IP lockout (spec §17.3): 10 failed attempts within 5 minutes → 15-minute lockout. bcrypt-12
            // makes each verify costly; the lockout caps sustained online brute force on top of that.
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.IsLocked(ip, out var retryAfter))
            {
                ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var hash = await config.GetAsync(PasswordHashKey, ctx.RequestAborted);
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(req.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, hash))
            {
                throttle.RecordFailure(ip);
                return Results.Unauthorized();
            }

            throttle.RecordSuccess(ip);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        // First-run setup or password change. Allowed without auth ONLY when no password exists yet
        // (bootstrap); afterwards the caller must already be authenticated.
        group.MapPost("/auth/password", async (SetPasswordRequest req, HttpContext ctx, IConfigRepository config) =>
        {
            var hasPassword = !string.IsNullOrEmpty(await config.GetAsync(PasswordHashKey, ctx.RequestAborted));
            if (hasPassword && !(ctx.User.Identity?.IsAuthenticated ?? false))
                return Results.Unauthorized();
            if (ValidatePassword(req.Password) is { } error)
                return Results.BadRequest(new { error });

            await config.SetAsync(PasswordHashKey, BCrypt.Net.BCrypt.HashPassword(req.Password, 12), encrypted: false, ctx.RequestAborted);

            // Sign the admin in immediately after first-run setup.
            if (!hasPassword)
            {
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            }
            return Results.Ok(new { ok = true });
        });
    }

    // A small built-in list of the most common weak passwords (spec §17.3 calls for a common-password
    // list check). Compared case-insensitively.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "12345678", "123456789", "1234567890",
        "qwerty123", "qwertyuiop", "letmein1", "iloveyou1", "admin123", "welcome1", "abc12345",
        "1q2w3e4r", "1qaz2wsx", "zaq12wsx", "trustno1", "dragon123", "monkey123", "football1",
    };

    /// <summary>
    /// Enforces the web-UI password policy (spec §17.3): minimum 12 characters with at least one
    /// uppercase letter, one lowercase letter and one digit, and not in the common-password list.
    /// Returns a human-readable error message when the password is rejected, or <c>null</c> when it passes.
    /// </summary>
    internal static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            return "Password must be at least 12 characters.";
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            return "Password must contain at least one uppercase letter, one lowercase letter and one digit.";
        if (CommonPasswords.Contains(password))
            return "Password is too common; choose a less predictable password.";
        return null;
    }

    private sealed record LoginRequest(string Password);
    private sealed record SetPasswordRequest(string Password);
}
