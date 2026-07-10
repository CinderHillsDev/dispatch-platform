using Dispatch.Core.Smtp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dispatch.Web.Endpoints;

/// <summary>Manages the SMTP AUTH sender allow-list (spec §5.3).</summary>
public static class SmtpCredentialEndpoints
{
    public static void MapSmtpCredentials(this RouteGroupBuilder group)
    {
        group.MapGet("/smtp-credentials", async (ISmtpCredentialRepository creds, CancellationToken ct) =>
            Results.Ok((await creds.ListAsync(ct)).Select(c => new { c.Id, c.Username, c.CreatedAt, c.LastUsedAt })));

        group.MapPost("/smtp-credentials", async (AddCredentialRequest req, ISmtpCredentialRepository creds, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Username and password are required." });
            await creds.AddAsync(req.Username.Trim(), req.Password, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapDelete("/smtp-credentials/{username}", async (string username, ISmtpCredentialRepository creds, CancellationToken ct) =>
            await creds.DeleteAsync(username, ct) ? Results.Ok(new { ok = true }) : Results.NotFound());
    }

    private sealed record AddCredentialRequest(string Username, string Password);
}
