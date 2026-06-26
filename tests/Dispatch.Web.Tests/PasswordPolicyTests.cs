using System.Net;
using System.Net.Http.Json;
using Dispatch.Web.Auth;

namespace Dispatch.Web.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdef123456")]  // 12 chars, upper+lower+digit
    [InlineData("Str0ngPassphrase")]
    public void Accepts_compliant_passwords(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ab1cdef")]        // 7 chars — too short
    [InlineData("Abcdef12345")]    // 11 chars — just under the 12-char minimum
    public void Rejects_short_passwords(string? password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("12 characters", error);
    }

    [Theory]
    [InlineData("abcdefghij12")]   // no uppercase
    [InlineData("ABCDEFGHIJ12")]   // no lowercase
    [InlineData("Abcdefghijkl")]   // no digit
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

    [Theory]
    [InlineData("MyQwerty12345")]      // long & char-class-compliant but embeds "qwerty"
    [InlineData("Summerfootball9")]    // embeds "football"
    [InlineData("Trustno1Always")]     // embeds "trustno1"
    [InlineData("Abc123456789xy")]     // embeds the "123456789" sequence
    public void Rejects_predictable_base_tokens_even_when_otherwise_compliant(string password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("common", error);   // "common, easily-guessed word or sequence"
    }

    [Theory]
    [InlineData("Brightolive47kx")]    // 12+ chars, upper+lower+digit, no common token
    [InlineData("Velvet9Harbor2x")]
    public void Accepts_unpredictable_compliant_passwords(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
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
