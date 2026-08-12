using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

public sealed class AuthEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record AuthPayload(string AccessToken, string RefreshToken, int ExpiresInSeconds);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<AuthPayload> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = ApiFactory.AdminEmail,
            password = ApiFactory.AdminPassword
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
    }

    [Fact]
    public async Task Correct_credentials_return_both_tokens()
    {
        var client = factory.CreateClient();

        var auth = await LoginAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
        Assert.Equal(900, auth.ExpiresInSeconds);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_are_indistinguishable()
    {
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = "definitely-not-it" });

        var unknownEmail = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "stranger@nowhere.test", password = "definitely-not-it" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // Same status is not enough — the bodies must match too, or the difference
        // still tells an attacker which accounts exist.
        var first = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>();
        var second = await unknownEmail.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("title").GetString(), second.GetProperty("title").GetString());
        Assert.Equal("INVALID_CREDENTIALS", first.GetProperty("errorCode").GetString());
        Assert.Equal("INVALID_CREDENTIALS", second.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_protected_endpoint_rejects_an_anonymous_call_and_accepts_a_token()
    {
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var auth = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var authenticated = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task Refreshing_issues_a_new_pair()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });

        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;

        Assert.NotEqual(auth.RefreshToken, rotated.RefreshToken);
    }

    [Fact]
    public async Task Reusing_a_spent_refresh_token_kills_the_whole_family()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);

        // Spend it once, legitimately.
        var rotatedResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        var rotated = (await rotatedResponse.Content.ReadFromJsonAsync<AuthPayload>(Json))!;

        // Now replay the old one, as a thief holding a stolen copy would.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The legitimate holder's newest token must die too — that is the point of
        // family revocation. Without this assertion the test passes on a broken system.
        var afterRevocation = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    [Fact]
    public async Task Logging_out_revokes_the_presented_token()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_returns_the_same_answer_for_known_and_unknown_addresses()
    {
        var client = factory.CreateClient();

        var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new { email = ApiFactory.AdminEmail });
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new { email = "stranger@nowhere.test" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }

    [Fact]
    public async Task A_paused_account_is_blocked_on_the_very_next_request()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        await SetAdminStatusAsync(UserStatus.Paused);
        try
        {
            // The access token is still cryptographically valid and unexpired. Only the
            // per-request status check closes this window (BR-10).
            var blocked = await client.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

            var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ACCOUNT_PAUSED", body.GetProperty("errorCode").GetString());
        }
        finally
        {
            await SetAdminStatusAsync(UserStatus.Active);
        }
    }

    private async Task SetAdminStatusAsync(UserStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var admin = await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail);

        if (status == UserStatus.Paused)
            admin.Pause(DateTime.UtcNow);
        else
            admin.Reactivate(DateTime.UtcNow);

        await db.SaveChangesAsync();
    }
}
