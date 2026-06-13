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

        group.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IConfigRepository config) =>
        {
            var hash = await config.GetAsync(PasswordHashKey, ctx.RequestAborted);
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(req.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, hash))
                return Results.Unauthorized();

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
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });

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

    private sealed record LoginRequest(string Password);
    private sealed record SetPasswordRequest(string Password);
}
