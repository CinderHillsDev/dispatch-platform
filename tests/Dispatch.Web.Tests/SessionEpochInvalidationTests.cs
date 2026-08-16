using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dispatch.Core.Configuration;
using Dispatch.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Web.Tests;

/// <summary>
/// Regression test for a real bug found while hardening reset-admin-password: OnValidatePrincipal used to
/// read the session epoch from ConfigCache, an in-memory snapshot that is only refreshed within the same
/// request that itself wrote a config change (see the /config/* endpoints). A change written by a SEPARATE
/// process - exactly what reset-admin-password is, by design - was invisible to an already-running service,
/// so a "reset" never actually invalidated the session it was meant to kill. Verified manually against a
/// real running service before this was fixed (old cookie stayed authenticated:true after a CLI reset) and
/// after (correctly flips to false); this exercises the same code path (OnValidatePrincipal via a real
/// cookie-auth request) without needing a second OS process.
///
/// Uses its own HttpClient (not the shared host.Web) so its cookie never leaks into other tests sharing
/// this WebTestHost, and restores config state in `finally` so later tests in the collection still see the
/// "no password configured" first-run state they assume (see PasswordPolicyEndpointTests).
/// </summary>
[Collection("web")]
public class SessionEpochInvalidationTests(WebTestHost host)
{
    [Fact]
    public async Task Bumping_the_session_epoch_via_the_repository_invalidates_an_already_issued_cookie()
    {
        var config = host.Services.GetRequiredService<IConfigRepository>();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{WebTestHost.WebPort}") };
        try
        {
            var setRes = await client.PostAsJsonAsync("/api/auth/password", new { password = "Brightolive47kx" });
            Assert.Equal(HttpStatusCode.OK, setRes.StatusCode);

            var loginRes = await client.PostAsJsonAsync("/api/auth/login", new { password = "Brightolive47kx" });
            Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

            var before = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
            Assert.True(before.GetProperty("authenticated").GetBoolean());

            // Simulate reset-admin-password: write directly through IConfigRepository, exactly as a
            // SEPARATE process would - never touching this process's ConfigCache.
            await config.SetAsync(ConfigKeys.WebUiSessionEpoch, "1");

            var after = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
            Assert.False(after.GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            await config.SetAsync(AuthEndpoints.PasswordHashKey, "");
            await config.SetAsync(ConfigKeys.WebUiSessionEpoch, "0");
        }
    }
}
