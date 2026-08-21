using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own factory with a deliberately tiny limit. The shared ApiFactory raises the limit so
/// the suite does not throttle itself, which would otherwise leave this protection untested.
/// </summary>
public sealed class ThrottledApiFactory : ApiFactory
{
    public const int Limit = 3;
    public const int PerEmailLimit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:InvitationValidationPerHour"] = Limit.ToString(),
                ["RateLimits:PasswordResetPerEmailPerHour"] = PerEmailLimit.ToString(),
                // Deliberately generous, so the per-email tests below cannot trip the per-IP
                // limit instead and pass for the wrong reason.
                ["RateLimits:PasswordResetPerIpPerHour"] = "10000"
            }));
    }
}

public sealed class RateLimitTests(ThrottledApiFactory factory) : IClassFixture<ThrottledApiFactory>
{
    [Fact]
    public async Task Invitation_code_guessing_is_throttled()
    {
        var client = factory.CreateClient();

        // An invitation code is short enough to type, so an unthrottled validate endpoint
        // would be a guessing oracle (architecture section 7.1).
        for (var attempt = 0; attempt < ThrottledApiFactory.Limit; attempt++)
        {
            var allowed = await client.GetAsync("/api/v1/invitations/validate?code=WRONG-CODES");
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var blocked = await client.GetAsync("/api/v1/invitations/validate?code=WRONG-CODES");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TOO_MANY_REQUESTS", body.GetProperty("errorCode").GetString());
        Assert.True(body.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    // ------------------------------------------------- password reset per email

    private Task<HttpResponseMessage> ForgotAsync(string email) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });

    [Fact]
    public async Task Password_reset_requests_are_throttled_per_email()
    {
        const string email = "reset.throttled@nowhere.test";

        for (var attempt = 0; attempt < ThrottledApiFactory.PerEmailLimit; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(email)).StatusCode);

        var blocked = await ForgotAsync(email);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TOO_MANY_REQUESTS", body.GetProperty("errorCode").GetString());

        var retryAfter = body.GetProperty("retryAfterSeconds").GetInt32();
        Assert.True(retryAfter > 0);

        // Body and header must agree, or a client honouring one waits the wrong amount.
        Assert.Equal(retryAfter, (int)blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
    }

    [Fact]
    public async Task Throttling_one_address_does_not_throttle_another()
    {
        const string spent = "reset.spent@nowhere.test";

        for (var attempt = 0; attempt <= ThrottledApiFactory.PerEmailLimit; attempt++)
            await ForgotAsync(spent);

        // Otherwise one abusive address would lock every other athlete out of password reset.
        var other = await ForgotAsync("reset.innocent@nowhere.test");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Case_and_spacing_cannot_buy_extra_attempts()
    {
        string[] spellings =
        [
            "reset.casing@nowhere.test",
            "Reset.Casing@Nowhere.Test",
            "  reset.casing@nowhere.test  "
        ];

        foreach (var spelling in spellings)
            Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(spelling)).StatusCode);

        // Three spellings of one address is three attempts, not three fresh allowances.
        var blocked = await ForgotAsync("RESET.CASING@NOWHERE.TEST");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task The_throttle_reveals_nothing_about_which_addresses_are_registered()
    {
        // The seeded Admin exists; the other address does not. Both must behave identically,
        // or a 429 that only appears for real accounts becomes an enumeration oracle - which
        // is exactly what this endpoint's always-200 design exists to prevent.
        var registered = ApiFactory.AdminEmail;
        const string unknown = "definitely.not.a.user@nowhere.test";

        for (var attempt = 0; attempt < ThrottledApiFactory.PerEmailLimit; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(registered)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(unknown)).StatusCode);
        }

        var blockedRegistered = await ForgotAsync(registered);
        var blockedUnknown = await ForgotAsync(unknown);

        Assert.Equal(HttpStatusCode.TooManyRequests, blockedRegistered.StatusCode);
        Assert.Equal(blockedRegistered.StatusCode, blockedUnknown.StatusCode);

        // Everything but the correlation id, which is per-request by design and carries nothing
        // about the address.
        Assert.Equal(
            await ComparableBodyAsync(blockedRegistered),
            await ComparableBodyAsync(blockedUnknown));
    }

    private static async Task<string> ComparableBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return string.Join('|', body.EnumerateObject()
            .Where(p => p.Name is not "correlationId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));
    }
}
