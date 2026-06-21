using System.Net;
using System.Security.Claims;
using Dispatch.Core.Configuration;
using Dispatch.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Tests;

public class WebAuthMiddlewareTests
{
    private const int WebPort = 8420;

    private static (HttpContext ctx, Func<bool> nextCalled) Context(
        string path, int port, bool authenticated, string? remoteIp = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        ctx.Request.Method = "GET";   // real requests always have a method; tests override for mutating cases
        if (remoteIp is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        ctx.Request.Path = path;
        if (authenticated)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "cookie"));
        var called = false;
        ctx.Items["__next"] = (RequestDelegate)(_ => { called = true; return Task.CompletedTask; });
        return (ctx, () => called);
    }

    private static WebAuthMiddleware Middleware(string? allowedCidrs = null)
    {
        var cache = new ConfigCache();
        var values = new Dictionary<string, string> { [ConfigKeys.WebUiPort] = WebPort.ToString() };
        if (allowedCidrs is not null) values[ConfigKeys.WebUiAllowedCidrs] = allowedCidrs;
        cache.LoadFrom(values);
        return new WebAuthMiddleware(cache);
    }

    private static Task Invoke(HttpContext ctx, string? allowedCidrs = null) =>
        Middleware(allowedCidrs).InvokeAsync(ctx, (RequestDelegate)ctx.Items["__next"]!);

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
    public async Task Blocks_authenticated_mutating_api_without_csrf_header()
    {
        var (ctx, nextCalled) = Context("/api/keys", WebPort, authenticated: true);
        ctx.Request.Method = "POST";   // no X-Dispatch-Request header → CSRF guard rejects
        await Invoke(ctx);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.False(nextCalled());
    }

    [Fact]
    public async Task Allows_authenticated_mutating_api_with_csrf_header()
    {
        var (ctx, nextCalled) = Context("/api/keys", WebPort, authenticated: true);
        ctx.Request.Method = "POST";
        ctx.Request.Headers[WebAuthMiddleware.CsrfHeader] = "1";
        await Invoke(ctx);
        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Allows_authenticated_GET_without_csrf_header()
    {
        // Reads are not state-changing, so the CSRF header is not required.
        var (ctx, nextCalled) = Context("/api/stats", WebPort, authenticated: true);
        ctx.Request.Method = "GET";
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
        var (ctx, nextCalled) = Context("/api/v1/messages", 8025, authenticated: false);
        await Invoke(ctx);
        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Blocks_source_ip_outside_allow_list()
    {
        // An operator-tightened allow-list must 403 a source IP outside it — even for the open /health path.
        var (ctx, nextCalled) = Context("/health", WebPort, authenticated: false, remoteIp: "203.0.113.9");
        await Invoke(ctx, allowedCidrs: "[\"10.0.0.0/8\"]");
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.False(nextCalled());
    }

    [Fact]
    public async Task Allows_source_ip_inside_allow_list()
    {
        var (ctx, nextCalled) = Context("/health", WebPort, authenticated: false, remoteIp: "10.1.2.3");
        await Invoke(ctx, allowedCidrs: "[\"10.0.0.0/8\"]");
        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("172.17.0.1")]   // Docker bridge gateway (the headless/container regression)
    [InlineData("203.0.113.9")]  // arbitrary public client
    public async Task Allows_any_source_ip_when_allow_list_empty(string remoteIp)
    {
        // Regression guard for the deployment-friendly default: an empty webui allow-list (the seeded
        // default) must let a NAT'd/headless request through, otherwise the dashboard is unreachable.
        var (ctx, nextCalled) = Context("/health", WebPort, authenticated: false, remoteIp: remoteIp);
        await Invoke(ctx, allowedCidrs: "[]");
        Assert.True(nextCalled());
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }
}
