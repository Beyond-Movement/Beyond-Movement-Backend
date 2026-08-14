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
    private sealed record AuthPayload(
        string AccessToken, string RefreshToken, int ExpiresInSeconds, int RefreshExpiresInSeconds);

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

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    // ---------------------------------------------------------------- login

    [Fact]
    public async Task Correct_credentials_return_both_tokens_and_both_expiries()
    {
        var client = factory.CreateClient();

        var auth = await LoginAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
        Assert.Equal(900, auth.ExpiresInSeconds);
        Assert.Equal(30 * 24 * 60 * 60, auth.RefreshExpiresInSeconds);
    }

    [Fact]
    public async Task Login_reports_profile_completion_so_the_app_can_route_without_a_second_call()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = ApiFactory.AdminEmail,
            password = ApiFactory.AdminPassword
        });

        var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");

        // The Admin has no Complete Profile step, so this is always true for them.
        Assert.True(user.GetProperty("profileCompleted").GetBoolean());
    }

    [Fact]
    public async Task Refreshing_carries_profile_completion_too()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });

        var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");

        Assert.True(user.GetProperty("profileCompleted").GetBoolean());
    }

    [Fact]
    public async Task Google_sign_in_carries_profile_completion_too()
    {
        factory.GoogleValidator.NextIdentity = new Modules.Identity.Services.GoogleIdentity(
            "google-sub-profile-flag", ApiFactory.AdminEmail, true, "Integration Admin");

        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/google", new { idToken = "any" });

        var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");

        Assert.True(user.GetProperty("profileCompleted").GetBoolean());
    }

    [Fact]
    public async Task Every_error_carries_an_error_code_and_a_correlation_id()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = "definitely-not-it" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("INVALID_CREDENTIALS", body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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
    public async Task Validation_failures_report_the_offending_field()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "not-an-email", password = "" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("errorCode").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("Email", out _));
    }

    // ------------------------------------------------------- current user

    [Fact]
    public async Task Current_user_returns_everything_needed_to_restore_a_session()
    {
        var client = await SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/auth/me");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(ApiFactory.AdminEmail, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("profileCompleted").GetBoolean());
        Assert.Equal("1.0.0", body.GetProperty("minimumSupportedAppVersion").GetString());
    }

    [Fact]
    public async Task Current_user_rejects_an_anonymous_call()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------- refresh

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

        var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REFRESH_TOKEN", body.GetProperty("errorCode").GetString());

        // The legitimate holder's newest token must die too — that is the point of
        // family revocation. Without this assertion the test passes on a broken system.
        var afterRevocation = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    // -------------------------------------------------------------- logout

    [Fact]
    public async Task Logout_requires_authentication()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);

        // No bearer token: architecture section 14.1 marks logout as authenticated.
        var anonymous = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = auth.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Logging_out_revokes_the_presented_token()
    {
        var client = factory.CreateClient();
        var auth = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    // ------------------------------------------------------ password reset

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
    public async Task A_weak_or_common_password_is_rejected_on_reset()
    {
        var client = factory.CreateClient();

        var tooShort = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token = "irrelevant", newPassword = "short" });
        var tooCommon = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token = "irrelevant", newPassword = "password123" });

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooCommon.StatusCode);

        var body = await tooCommon.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_unknown_reset_token_is_refused()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token = "never-issued", newPassword = "Perfectly#Fine2026" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_RESET_TOKEN", body.GetProperty("errorCode").GetString());
    }

    // ----------------------------------------------------- change password

    [Fact]
    public async Task Change_password_rejects_a_wrong_current_password()
    {
        var client = await SignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = "not-the-current-one", newPassword = "Perfectly#Fine2026" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = ApiFactory.AdminPassword, newPassword = "Perfectly#Fine2026" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------- Google SSO

    [Fact]
    public async Task Google_sign_in_refuses_a_token_that_fails_verification()
    {
        factory.GoogleValidator.NextIdentity = null;
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "not-a-real-token" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_GOOGLE_TOKEN", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Google_sign_in_refuses_an_unverified_google_email()
    {
        factory.GoogleValidator.NextIdentity =
            new Modules.Identity.Services.GoogleIdentity("google-sub-1", ApiFactory.AdminEmail, false, "Admin");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "any" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_GOOGLE_TOKEN", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Google_sign_in_never_creates_an_account_for_a_stranger()
    {
        factory.GoogleValidator.NextIdentity = new Modules.Identity.Services.GoogleIdentity(
            "google-sub-stranger", "stranger@nowhere.test", true, "A Stranger");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "any" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // BR-01: the platform is invitation-only. Google authenticates, it never registers.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("INVITATION_REQUIRED", body.GetProperty("errorCode").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == "stranger@nowhere.test"));
    }

    [Fact]
    public async Task Google_sign_in_links_to_an_existing_account_with_the_same_verified_email()
    {
        factory.GoogleValidator.NextIdentity = new Modules.Identity.Services.GoogleIdentity(
            "google-sub-admin", ApiFactory.AdminEmail, true, "Integration Admin");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "any" });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail);
        Assert.Equal("google-sub-admin", admin.GoogleSubjectId);

        // And a second sign-in now matches on the subject rather than the email.
        var again = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "any" });
        again.EnsureSuccessStatusCode();
    }

    // -------------------------------------------------------------- paused

    [Fact]
    public async Task A_paused_account_is_blocked_on_the_very_next_request()
    {
        var client = await SignedInClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        await SetAdminStatusAsync(UserStatus.Paused);
        try
        {
            // The access token is still cryptographically valid and unexpired. Only the
            // per-request status check closes this window (BR-10).
            var blocked = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

            var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ACCOUNT_PAUSED", body.GetProperty("errorCode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
        }
        finally
        {
            await SetAdminStatusAsync(UserStatus.Active);
        }
    }

    [Fact]
    public async Task A_paused_account_cannot_log_in()
    {
        await SetAdminStatusAsync(UserStatus.Paused);
        try
        {
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
