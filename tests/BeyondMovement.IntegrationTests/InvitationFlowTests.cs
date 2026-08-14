using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The whole invited-athlete journey: the coach invites, the athlete validates the emailed
/// code, creates an account with a password or with Google, and completes their profile.
/// </summary>
public sealed class InvitationFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);
    private sealed record ValidatePayload(string Email, DateTime ExpiresAtUtc, string RegistrationToken);

    /// <summary>
    /// Like EnsureSuccessStatusCode, but puts the problem body in the failure message. A bare
    /// "400 Bad Request" tells you nothing about which rule rejected the request.
    /// </summary>
    private static async Task AssertSucceededAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        Assert.Fail($"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = ApiFactory.AdminEmail,
            password = ApiFactory.AdminPassword
        });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>
    /// Invites the address and returns the raw code. The API never reveals it — only the
    /// athlete's inbox does — so the test reads the stored hash and reverses the lookup the
    /// same way validation does.
    /// </summary>
    private async Task<(Guid invitationId, string code)> InviteAsync(HttpClient admin, string email)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/invitations", new { email });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        Assert.False(body.TryGetProperty("code", out _), "the raw code must never be returned");

        return (id, await FindCodeAsync(id));
    }

    // The email stub logs the code; rather than parse logs, brute-force the tiny space of
    // codes this test issued by hashing candidates is impossible — so read it from the
    // outbox the stub writes into instead.
    private async Task<string> FindCodeAsync(Guid invitationId)
    {
        using var scope = factory.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<TestEmailOutbox>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invitation = await db.Invitations.AsNoTracking().SingleAsync(i => i.Id == invitationId);
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();

        var code = outbox.Messages
            .SelectMany(m => m.TextBody.Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(word => word.Trim())
            .FirstOrDefault(word => tokens.Hash(InvitationCode.Normalize(word)) == invitation.CodeHash);

        Assert.NotNull(code);
        return code!;
    }

    private async Task<ValidatePayload> ValidateAsync(string code)
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/v1/invitations/validate?code={Uri.EscapeDataString(code)}");

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ValidatePayload>(Json))!;
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task An_invited_athlete_can_register_with_a_password_and_complete_their_profile()
    {
        var admin = await AdminClientAsync();
        const string email = "athlete.password@nowhere.test";

        var (_, code) = await InviteAsync(admin, email);

        var validated = await ValidateAsync(code);
        Assert.Equal(email, validated.Email);
        Assert.False(string.IsNullOrWhiteSpace(validated.RegistrationToken));

        var client = factory.CreateClient();
        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            password = "Athlete#Strong2026",
            fullName = "Alex Thompson"
        });

        await AssertSucceededAsync(registerResponse);
        var body = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Athlete", body.GetProperty("user").GetProperty("role").GetString());

        var accessToken = body.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Registration signs the athlete in, but the app must route to Complete Profile.
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(me.GetProperty("profileCompleted").GetBoolean());

        var profileResponse = await client.PostAsJsonAsync("/api/v1/athletes/me/profile", new
        {
            fullName = "Alex Thompson",
            dateOfBirth = "2001-04-17",
            gender = "Male",
            sport = "Tennis"
        });

        profileResponse.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(after.GetProperty("profileCompleted").GetBoolean());

        // And the athlete can now sign in normally with the password they chose.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "Athlete#Strong2026" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>
    /// The incomplete-then-complete transition, seen through the authentication responses the
    /// app actually routes on — not through /auth/me. This is what lets login decide between
    /// Home and Complete Profile without a follow-up request.
    /// </summary>
    [Fact]
    public async Task Authentication_responses_report_profile_completion_before_and_after()
    {
        var admin = await AdminClientAsync();
        const string email = "athlete.routing@nowhere.test";
        const string password = "Athlete#Strong2026";

        var (_, code) = await InviteAsync(admin, email);
        var validated = await ValidateAsync(code);

        var client = factory.CreateClient();

        // 1. Registration itself must say "not finished".
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            password,
            fullName = "Robin Vale"
        });
        await AssertSucceededAsync(registered);

        var registeredBody = await registered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(registeredBody.GetProperty("user").GetProperty("profileCompleted").GetBoolean());

        // 2. So must a fresh login before the profile is filled in.
        var beforeLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var beforeBody = await beforeLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(beforeBody.GetProperty("user").GetProperty("profileCompleted").GetBoolean());

        // 3. Complete it.
        var accessToken = registeredBody.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var completed = await client.PostAsJsonAsync("/api/v1/athletes/me/profile", new
        {
            fullName = "Robin Vale",
            dateOfBirth = "1999-02-11",
            gender = "Female",
            sport = "Swimming"
        });
        await AssertSucceededAsync(completed);

        // 4. Every later authentication response must now say "finished".
        var afterLogin = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var afterBody = await afterLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(afterBody.GetProperty("user").GetProperty("profileCompleted").GetBoolean());

        // ...including a refresh, which builds its payload the same way.
        var refreshed = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = afterBody.GetProperty("refreshToken").GetString() });
        var refreshedBody = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refreshedBody.GetProperty("user").GetProperty("profileCompleted").GetBoolean());
    }

    [Fact]
    public async Task An_invited_athlete_can_register_with_google_and_needs_no_password()
    {
        var admin = await AdminClientAsync();
        const string email = "athlete.google@nowhere.test";

        var (_, code) = await InviteAsync(admin, email);
        var validated = await ValidateAsync(code);

        factory.GoogleValidator.NextIdentity = new GoogleIdentity("google-sub-athlete", email, true, "Jordan Blake");

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            googleIdToken = "any"
        });

        await AssertSucceededAsync(response);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);

        Assert.Equal("google-sub-athlete", user.GoogleSubjectId);
        Assert.Null(user.PasswordHash);                 // no password on the Google path
        Assert.Equal("Jordan Blake", user.FullName);    // prefilled from Google, editable later
    }

    // -------------------------------------------------------- the guard rails

    [Fact]
    public async Task Registration_creates_the_user_and_the_athlete_profile_together()
    {
        var admin = await AdminClientAsync();
        const string email = "athlete.profile@nowhere.test";

        var (_, code) = await InviteAsync(admin, email);
        var validated = await ValidateAsync(code);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            password = "Athlete#Strong2026",
            fullName = "Pat Rivers"
        });
        await AssertSucceededAsync(response);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);

        // "A valid invitation creates exactly one athlete account" — user and profile together.
        Assert.True(await db.AthleteProfiles.AnyAsync(p => p.UserId == user.Id));
        Assert.Equal(user.CoachId, (await db.AthleteProfiles.AsNoTracking()
            .SingleAsync(p => p.UserId == user.Id)).CoachId);
    }

    [Fact]
    public async Task Validating_a_code_does_not_consume_the_invitation()
    {
        var admin = await AdminClientAsync();
        var (id, code) = await InviteAsync(admin, "athlete.twice@nowhere.test");

        await ValidateAsync(code);
        await ValidateAsync(code);   // the athlete backed out and came back

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitation = await db.Invitations.AsNoTracking().SingleAsync(i => i.Id == id);

        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    public async Task An_invitation_cannot_be_redeemed_twice()
    {
        var admin = await AdminClientAsync();
        const string email = "athlete.once@nowhere.test";

        var (_, code) = await InviteAsync(admin, email);
        var validated = await ValidateAsync(code);

        var client = factory.CreateClient();
        var payload = new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            password = "Athlete#Strong2026",
            fullName = "Sam Reed"
        };

        var first = await client.PostAsJsonAsync("/api/v1/auth/register", payload);
        await AssertSucceededAsync(first);

        var second = await client.PostAsJsonAsync("/api/v1/auth/register", payload);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("INVITATION_USED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_google_account_cannot_redeem_someone_elses_invitation()
    {
        var admin = await AdminClientAsync();
        const string invited = "athlete.invited@nowhere.test";

        var (_, code) = await InviteAsync(admin, invited);
        var validated = await ValidateAsync(code);

        // A verified Google account, but for a different address.
        factory.GoogleValidator.NextIdentity =
            new GoogleIdentity("google-sub-other", "someone.else@nowhere.test", true, "Someone Else");

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            googleIdToken = "any"
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("GOOGLE_EMAIL_MISMATCH", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Registration_requires_accepting_the_terms()
    {
        var admin = await AdminClientAsync();
        var (_, code) = await InviteAsync(admin, "athlete.terms@nowhere.test");
        var validated = await ValidateAsync(code);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = false,
            password = "Athlete#Strong2026",
            fullName = "Terms Refuser"
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TERMS_NOT_ACCEPTED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Registration_refuses_both_a_password_and_google_together()
    {
        var admin = await AdminClientAsync();
        var (_, code) = await InviteAsync(admin, "athlete.both@nowhere.test");
        var validated = await ValidateAsync(code);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            registrationToken = validated.RegistrationToken,
            termsAccepted = true,
            password = "Athlete#Strong2026",
            googleIdToken = "any",
            fullName = "Both Ways"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_invitation_cannot_be_validated()
    {
        var admin = await AdminClientAsync();
        var (id, code) = await InviteAsync(admin, "athlete.revoked@nowhere.test");

        var revoke = await admin.DeleteAsync($"/api/v1/invitations/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var response = await factory.CreateClient()
            .GetAsync($"/api/v1/invitations/validate?code={Uri.EscapeDataString(code)}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVITATION_REVOKED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_unknown_code_is_refused()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/invitations/validate?code=ZZZZZ-ZZZZZ");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVITATION_INVALID", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Resending_replaces_the_previous_code()
    {
        var admin = await AdminClientAsync();
        var (id, originalCode) = await InviteAsync(admin, "athlete.resend@nowhere.test");

        var resend = await admin.PostAsync($"/api/v1/invitations/{id}/resend", null);
        resend.EnsureSuccessStatusCode();

        // The first code must stop working, or a resend would leave two live codes.
        var response = await factory.CreateClient()
            .GetAsync($"/api/v1/invitations/validate?code={Uri.EscapeDataString(originalCode)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient()
                .GetAsync($"/api/v1/invitations/validate?code={Uri.EscapeDataString(await FindCodeAsync(id))}"))
            .StatusCode);
    }

    // ----------------------------------------------------------- authorization

    [Fact]
    public async Task Only_an_admin_can_invite()
    {
        var anonymous = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/invitations", new { email = "nope@nowhere.test" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Inviting_an_address_that_already_has_an_account_is_refused()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/invitations", new { email = ApiFactory.AdminEmail });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("EMAIL_ALREADY_REGISTERED", body.GetProperty("errorCode").GetString());
    }
}
