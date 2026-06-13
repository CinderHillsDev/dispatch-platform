using System.Net;
using System.Net.Http.Json;
using Dispatch.Web.Auth;

namespace Dispatch.Web.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdef12")]      // 8 chars, upper+lower+digit
    [InlineData("Str0ngPassphrase")]
    public void Accepts_compliant_passwords(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ab1cdef")]       // 7 chars — too short
    public void Rejects_short_passwords(string? password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("8 characters", error);
    }

    [Theory]
    [InlineData("abcdefg1")]      // no uppercase
    [InlineData("ABCDEFG1")]      // no lowercase
    [InlineData("Abcdefgh")]      // no digit
    public void Rejects_passwords_missing_character_classes(string password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("uppercase", error);
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("Password123")]   // matched case-insensitively
    [InlineData("12345678")]
    public void Rejects_common_passwords(string password)
    {
        Assert.NotNull(AuthEndpoints.ValidatePassword(password));
    }
}

[Collection("web")]
public class PasswordPolicyEndpointTests(WebTestHost host)
{
    [Fact]
    public async Task Set_password_rejects_weak_password_with_400()
    {
        // No password is configured in the test host, so /auth/password is reachable for first-run
        // setup. A non-compliant password must be rejected before anything is persisted.
        var res = await host.Web.PostAsJsonAsync("/api/auth/password", new { password = "weak" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
