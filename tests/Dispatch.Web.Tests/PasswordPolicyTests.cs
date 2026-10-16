using System.Net;
using System.Net.Http.Json;
using Dispatch.Web.Auth;

namespace Dispatch.Web.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Harbor9x")]           // 8 chars - exactly at the floor
    [InlineData("Abcdef123456")]
    [InlineData("Str0ngPassphrase")]
    [InlineData("quiet harbor lane 7")]  // spaces are fine; passphrases are the point
    public void Accepts_passwords_at_or_above_the_floor(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]                 // whitespace only, even if long enough
    [InlineData("Ab1cdef")]             // 7 chars
    [InlineData("Tide9x")]              // 6 chars
    public void Rejects_short_passwords(string? password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("8 characters", error);
    }

    // The uppercase/lowercase/digit rule was removed on purpose (see ValidatePassword). These would all have
    // failed the old policy and must pass now, so the removal is a tested decision rather than a drift.
    [Theory]
    [InlineData("alllowercaseandlong")]     // no uppercase, no digit
    [InlineData("ALLUPPERANDLONG")]
    [InlineData("ALLUPPERAND1234")]
    [InlineData("abcdefghijklmn")]
    public void Does_not_require_character_classes(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("Password123")]             // matched case-insensitively
    [InlineData("12345678")]                // 8 chars: clears the floor, still a bad password
    [InlineData("123456789")]
    [InlineData("administrator")]
    public void Rejects_common_passwords_even_when_long_enough(string password)
    {
        Assert.NotNull(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData("MyQwerty12345")]            // embeds "qwerty"
    [InlineData("Summerfootball9")]          // embeds "football"
    [InlineData("Trustno1Always")]            // embeds "trustno1"
    [InlineData("Abc123456789xy")]            // embeds the "123456789" run
    [InlineData("harborpasswordlane")]         // embeds "password", past the floor
    public void Rejects_predictable_base_tokens(string password)
    {
        var error = AuthEndpoints.ValidatePassword(password);
        Assert.NotNull(error);
        Assert.Contains("common", error);   // "common, easily-guessed word or sequence"
    }

    [Theory]
    [InlineData("Brightolive47kx")]
    [InlineData("Velvet9Harbor2x")]
    public void Accepts_unpredictable_passwords(string password)
    {
        Assert.Null(AuthEndpoints.ValidatePassword(password));
    }

    [Theory]
    [InlineData("letmein1")]      // 8 characters, on the exact list
    [InlineData("qwerty12")]      // 8 characters, embeds the "qwerty" token
    [InlineData("welcome8")]      // 8 characters, embeds "welcome"
    public void The_blacklist_carries_what_the_lower_floor_no_longer_does(string password)
    {
        // This is the only place the 12-to-8 change could have quietly loosened something. The floor used to
        // reject a lot of short dictionary shapes by length alone; now the checks below the floor have to
        // catch them, so they are pinned here rather than assumed.
        Assert.Equal(8, password.Length);
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

    [Fact]
    public async Task Set_password_accepts_an_eight_character_uncommon_password()
    {
        var res = await host.Web.PostAsJsonAsync("/api/auth/password", new { password = "Harbor9x" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
