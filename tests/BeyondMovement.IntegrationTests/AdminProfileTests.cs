using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own factory: these tests rename the seeded Admin, which other suites assert on.
/// </summary>
public sealed class AdminProfileApiFactory : ApiFactory;

/// <summary>
/// The Admin's own Personal Information screen — full name, email and phone, and the read-only
/// rule that keeps the login identity out of a profile form.
/// </summary>
public sealed class AdminProfileTests(AdminProfileApiFactory factory) : IClassFixture<AdminProfileApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);
    private sealed record Profile(Guid Id, string? FullName, string Email, string? Phone);

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

    private static async Task<Profile> GetProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/me/profile");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Profile>(Json))!;
    }

    // ------------------------------------------------------------------ read

    [Fact]
    public async Task The_profile_returns_name_email_and_phone_and_nothing_else()
    {
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/v1/auth/me/profile");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Pinned as a whole: the value of a dedicated profile shape is that it stays small, and
        // a field appearing here is a contract change the app has to be told about.
        Assert.Equal(
            ["id", "fullName", "email", "phone"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(ApiFactory.AdminEmail, body.GetProperty("email").GetString());
    }

    // ----------------------------------------------------------------- write

    [Fact]
    public async Task Editing_stores_both_fields_and_the_next_read_agrees()
    {
        var client = await AdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "Nadia Hassan", phone = "+20 100 123 4567" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = (await response.Content.ReadFromJsonAsync<Profile>(Json))!;
        Assert.Equal("Nadia Hassan", returned.FullName);
        Assert.Equal("+20 100 123 4567", returned.Phone);

        var reread = await GetProfileAsync(client);
        Assert.Equal("Nadia Hassan", reread.FullName);
        Assert.Equal("+20 100 123 4567", reread.Phone);
    }

    [Fact]
    public async Task Both_fields_are_stored_trimmed_so_the_app_should_render_from_the_response()
    {
        var client = await AdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "  Trimmed Coach  ", phone = "  +20 111 222 3333  " });

        var returned = (await response.Content.ReadFromJsonAsync<Profile>(Json))!;

        Assert.Equal("Trimmed Coach", returned.FullName);
        Assert.Equal("+20 111 222 3333", returned.Phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clearing_the_phone_stores_null_rather_than_an_empty_string(string? blank)
    {
        var client = await AdminClientAsync();

        await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "Nadia Hassan", phone = "+20 100 123 4567" });

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "Nadia Hassan", phone = blank });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // "" would render in the app as a number that is set but empty.
        Assert.Null((await response.Content.ReadFromJsonAsync<Profile>(Json))!.Phone);
        Assert.Null((await GetProfileAsync(client)).Phone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused_and_the_stored_one_survives(string blank)
    {
        var client = await AdminClientAsync();

        await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "Nadia Hassan", phone = (string?)null });

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = blank, phone = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());

        // The contract promises profileCompleted: true implies a non-null name, and the Admin
        // is complete from creation — so this edit must not be able to empty it.
        Assert.Equal("Nadia Hassan", (await GetProfileAsync(client)).FullName);
    }

    [Fact]
    public async Task A_nonsense_phone_number_is_refused()
    {
        var client = await AdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile",
            new { fullName = "Nadia Hassan", phone = "call me maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
    }

    // ------------------------------------------------------- email is read-only

    [Fact]
    public async Task The_email_cannot_be_changed_through_the_profile()
    {
        var client = await AdminClientAsync();

        // An email in the body is not part of the contract. Whether it is ignored or refused,
        // the one thing that must never happen is the login identity moving.
        var response = await client.PutAsJsonAsync("/api/v1/auth/me/profile", new
        {
            fullName = "Nadia Hassan",
            phone = (string?)null,
            email = "hijacked@nowhere.test"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApiFactory.AdminEmail, (await GetProfileAsync(client)).Email);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(db.Users.Any(u => u.Email == "hijacked@nowhere.test"));
        }

        // And the original address still signs in, which is the guarantee that actually matters.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = ApiFactory.AdminEmail,
            password = ApiFactory.AdminPassword
        });

        login.EnsureSuccessStatusCode();
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Reading_or_editing_the_profile_requires_a_token()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/auth/me/profile")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/v1/auth/me/profile",
                new { fullName = "Nobody", phone = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task An_athlete_cannot_reach_the_admin_profile()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, "profile.athlete@nowhere.test", "Profile Athlete",
                "Rowing", dateOfBirth: null);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "profile.athlete@nowhere.test",
            password = AthleteApiFactory.AthletePassword
        });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        // The athlete's own profile is /athletes/me/profile and has different fields. This
        // route is the Admin's, and is scoped that way deliberately.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/auth/me/profile")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync("/api/v1/auth/me/profile",
                new { fullName = "Profile Athlete", phone = (string?)null })).StatusCode);
    }
}
