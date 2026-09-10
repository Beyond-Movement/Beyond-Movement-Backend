using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests edit athletes down to the field, so nothing else may be reading
/// the rows they change.
/// </summary>
public sealed class AthleteProfileApiFactory : ApiFactory;

/// <summary>
/// The athlete's own Profile screen — the read behind it, and the edit behind it.
/// <para>
/// One route serves Complete Profile and Edit Profile, and it is a <b>full replacement</b>, so
/// the tests that matter most here are the ones pinning what happens to a field that was left
/// out. Email is returned and never written, which is the other half.
/// </para>
/// </summary>
public sealed class AthleteProfileTests(AthleteProfileApiFactory factory)
    : IClassFixture<AthleteProfileApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private sealed record Profile(
        Guid UserId, string? FullName, string Email, string? Phone,
        string? DateOfBirth, string? Gender, string? Sport, bool ProfileCompleted);

    // Static: xUnit builds a new instance of this class for every test method, so an
    // instance field would restart at zero and hand two tests the same address.
    private static int _counter;

    /// <summary>
    /// A signed-in athlete nobody else is using. <paramref name="complete"/> false leaves them
    /// registered but not finished, which is the state the read endpoint has to be honest about.
    /// </summary>
    private async Task<(HttpClient Client, string Email)> AthleteAsync(bool complete = true)
    {
        var email = $"profile{Interlocked.Increment(ref _counter)}@nowhere.test";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, complete ? "Alex Thompson" : null,
                complete ? "Tennis" : null,
                complete ? new DateOnly(2001, 4, 17) : null);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return (client, email);
    }

    private static async Task<Profile> GetProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/athletes/me/profile");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Profile>(Json))!;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/v1/athletes/me/profile", body);

    private static object FullProfile(string? phone = null) => new
    {
        fullName = "Alex Thompson",
        dateOfBirth = "2001-04-17",
        gender = "Male",
        sport = "Tennis",
        phone
    };

    // ------------------------------------------------------------------- read

    [Fact]
    public async Task The_profile_returns_the_six_fields_the_screen_shows_and_nothing_else()
    {
        var (client, email) = await AthleteAsync();

        var response = await client.GetAsync("/api/v1/athletes/me/profile");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Avatar is deliberately absent: it needs file storage, which is a phase of its own.
        Assert.Equal(
            ["userId", "fullName", "email", "phone", "dateOfBirth", "gender", "sport", "profileCompleted"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        var profile = body.Deserialize<Profile>(Json)!;

        Assert.Equal("Alex Thompson", profile.FullName);
        Assert.Equal(email, profile.Email);
        Assert.Equal("2001-04-17", profile.DateOfBirth);
        Assert.Equal("Tennis", profile.Sport);
        Assert.True(profile.ProfileCompleted);

        // Nothing has written one yet. This is the only field that may be null on a completed
        // profile, and it stays optional.
        Assert.Null(profile.Phone);
    }

    /// <summary>
    /// The state the endpoint exists to report honestly. An athlete who registered and stopped
    /// has no name, sport, gender or date of birth — that is what profileCompleted: false
    /// describes, not an error.
    /// </summary>
    [Fact]
    public async Task An_unfinished_profile_reads_back_as_nulls_rather_than_a_404()
    {
        var (client, email) = await AthleteAsync(complete: false);

        var profile = await GetProfileAsync(client);

        Assert.False(profile.ProfileCompleted);
        Assert.Null(profile.FullName);
        Assert.Null(profile.DateOfBirth);
        Assert.Null(profile.Gender);
        Assert.Null(profile.Sport);
        Assert.Null(profile.Phone);

        // The address is the one thing they always have — it is how they were invited.
        Assert.Equal(email, profile.Email);
    }

    // ------------------------------------------------------------------ write

    [Fact]
    public async Task Saving_the_profile_stores_every_field_and_reads_back_the_same()
    {
        var (client, _) = await AthleteAsync(complete: false);

        var response = await SaveAsync(client, new
        {
            fullName = "Robin Vale",
            dateOfBirth = "1999-02-11",
            gender = "Female",
            sport = "Swimming",
            phone = "+20 100 123 4567"
        });

        response.EnsureSuccessStatusCode();
        var returned = (await response.Content.ReadFromJsonAsync<Profile>(Json))!;

        Assert.Equal("Robin Vale", returned.FullName);
        Assert.Equal("+20 100 123 4567", returned.Phone);
        Assert.True(returned.ProfileCompleted);

        // The response must not be the only place it is true.
        var reread = await GetProfileAsync(client);

        Assert.Equal("Robin Vale", reread.FullName);
        Assert.Equal("1999-02-11", reread.DateOfBirth);
        Assert.Equal("Female", reread.Gender);
        Assert.Equal("Swimming", reread.Sport);
        Assert.Equal("+20 100 123 4567", reread.Phone);
        Assert.True(reread.ProfileCompleted);
    }

    [Fact]
    public async Task A_phone_number_is_stored_trimmed()
    {
        var (client, _) = await AthleteAsync();

        var response = await SaveAsync(client, FullProfile("  +20 111 222 3333  "));
        response.EnsureSuccessStatusCode();

        // Read back off the entity, not echoed, so the app renders what was stored.
        Assert.Equal("+20 111 222 3333",
            (await response.Content.ReadFromJsonAsync<Profile>(Json))!.Phone);
        Assert.Equal("+20 111 222 3333", (await GetProfileAsync(client)).Phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clearing_the_phone_stores_null_rather_than_an_empty_string(string? blank)
    {
        var (client, _) = await AthleteAsync();

        (await SaveAsync(client, FullProfile("+20 100 123 4567"))).EnsureSuccessStatusCode();

        var response = await SaveAsync(client, FullProfile(blank));
        response.EnsureSuccessStatusCode();

        // "" would render in the app as a phone number that is set but empty.
        Assert.Null((await response.Content.ReadFromJsonAsync<Profile>(Json))!.Phone);
        Assert.Null((await GetProfileAsync(client)).Phone);
    }

    /// <summary>
    /// Full replacement, not a patch. A client that omits the field is saying "no phone", and
    /// the contract says so in as many words — this is what makes that true rather than a
    /// promise nobody checks.
    /// </summary>
    [Fact]
    public async Task Omitting_the_phone_clears_it_rather_than_leaving_it_alone()
    {
        var (client, _) = await AthleteAsync();

        (await SaveAsync(client, FullProfile("+20 100 123 4567"))).EnsureSuccessStatusCode();
        Assert.Equal("+20 100 123 4567", (await GetProfileAsync(client)).Phone);

        var response = await SaveAsync(client, new
        {
            fullName = "Alex Thompson",
            dateOfBirth = "2001-04-17",
            gender = "Male",
            sport = "Tennis"
        });

        response.EnsureSuccessStatusCode();
        Assert.Null((await GetProfileAsync(client)).Phone);
    }

    [Fact]
    public async Task A_nonsense_phone_number_is_refused()
    {
        var (client, _) = await AthleteAsync();

        var response = await SaveAsync(client, FullProfile("call me maybe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());

        // The rest of the profile must survive a rejected edit.
        Assert.Equal("Tennis", (await GetProfileAsync(client)).Sport);
    }

    /// <summary>The same rule the Admin's profile applies — one column, one definition.</summary>
    [Fact]
    public async Task A_phone_number_longer_than_the_column_is_refused()
    {
        var (client, _) = await AthleteAsync();

        var response = await SaveAsync(client, FullProfile(new string('1', 41)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ email

    /// <summary>
    /// Email is display-only. There is no verification flow and no way to re-issue tokens for a
    /// new identity, so an address sent in the body must be ignored rather than honoured.
    /// </summary>
    [Fact]
    public async Task An_email_in_the_body_is_ignored_and_the_address_is_unchanged()
    {
        var (client, email) = await AthleteAsync();

        var response = await SaveAsync(client, new
        {
            fullName = "Alex Thompson",
            dateOfBirth = "2001-04-17",
            gender = "Male",
            sport = "Tennis",
            phone = (string?)null,
            email = "someone.else@nowhere.test"
        });

        response.EnsureSuccessStatusCode();

        Assert.Equal(email, (await response.Content.ReadFromJsonAsync<Profile>(Json))!.Email);
        Assert.Equal(email, (await GetProfileAsync(client)).Email);

        // ...and the old address still signs them in, because it is still their identity.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ----------------------------------------------------------------- access

    [Fact]
    public async Task Reading_or_editing_the_profile_requires_a_token()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/athletes/me/profile")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/athletes/me/profile", FullProfile())).StatusCode);
    }

    /// <summary>
    /// The mirror of the Admin-profile test. These are two screens with different fields, and
    /// each role reaches only its own.
    /// </summary>
    [Fact]
    public async Task An_admin_cannot_reach_the_athlete_profile()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/athletes/me/profile")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SaveAsync(client, FullProfile())).StatusCode);
    }

    /// <summary>
    /// Change Password and Log Out are the shared auth endpoints, not athlete-specific ones.
    /// This is the whole of the athlete's claim on them, and it is worth pinning because the
    /// Profile screen offers both and neither has a role policy to notice if one grew.
    /// </summary>
    [Fact]
    public async Task Change_password_and_log_out_work_for_an_athlete()
    {
        var email = $"authshared{Interlocked.Increment(ref _counter)}@nowhere.test";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, "Shared Auth", "Rowing", new DateOnly(2000, 1, 1));
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var loggedOut = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        // Sign in again — logging out revoked the refresh token, which is the point.
        var second = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });
        second.EnsureSuccessStatusCode();
        var reauth = (await second.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reauth.AccessToken);

        var changed = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = AthleteApiFactory.AthletePassword,
            newPassword = "Athlete#Changed2026"
        });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // Every refresh token is revoked, this device's included — the app must return to Login.
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = reauth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }
}
