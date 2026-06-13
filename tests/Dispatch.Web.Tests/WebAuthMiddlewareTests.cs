using System.Security.Claims;
using Dispatch.Core.Configuration;
using Dispatch.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Tests;

public class WebAuthMiddlewareTests
{
    private const int WebPort = 8420;

    private static (HttpContext ctx, Func<bool> nextCalled) Context(string path, int port, bool authenticated)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        ctx.Request.Path = path;
        if (authenticated)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "cookie"));
        var called = false;
        ctx.Items["__next"] = (RequestDelegate)(_ => { called = true; return Task.CompletedTask; });
        return (ctx, () => called);
    }

    private static WebAuthMiddleware Middleware() =>
        new(Options.Create(new WebUiOptions { Port = WebPort }));

    private static Task Invoke(HttpContext ctx) =>
        Middleware().InvokeAsync(ctx, (RequestDelegate)ctx.Items["__next"]!);

    [Fact]
    public async Task Blocks_unauthenticated_protected_api()
    {
        var (ctx, nextCalled) = Context("/api/stats", WebPort, authenticated: false);
        await Invoke(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(nextCalled());
    }

    [Fact]
    public async Task Allows_authenticated_protected_api()
    {
        var (ctx, nextCalled) = Context("/api/stats", WebPort, authenticated: true);
        await Invoke(ctx);
        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Allows_auth_endpoints_without_login()
    {
        var (ctx, nextCalled) = Context("/api/auth/login", WebPort, authenticated: false);
        await Invoke(ctx);
        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Ignores_other_ports()
    {
        // Ingestion port (different) is governed by API keys, not the cookie — pass through.
        var (ctx, nextCalled) = Context("/api/v1/messages", 8421, authenticated: false);
        await Invoke(ctx);
        Assert.True(nextCalled());
    }
}
