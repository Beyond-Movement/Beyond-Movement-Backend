using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// A factory with no pre-seeded athletes. These tests create their own, so they cannot disturb
/// the fixed set <see cref="AthleteApiFactory"/> provides for list and sort assertions.
/// </summary>
public sealed class AthleteAccessApiFactory : ApiFactory;

/// <summary>
/// Pausing and reactivating an athlete, who may reach the Admin endpoints, and the coach's
/// saved athlete-list sort.
/// </summary>
public sealed class AthleteAccessTests(AthleteAccessApiFactory factory) : IClassFixture<AthleteAccessApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);
    private sealed record SignedInAthlete(Guid UserId, string AccessToken, string RefreshToken);

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

    private async Task<SignedInAthlete> SeedSignedInAthleteAsync(string email, string name)
    {
        Guid userId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            userId = await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, name, "Rowing", dateOfBirth: null);
        }

        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;

        return new SignedInAthlete(userId, auth.AccessToken, auth.RefreshToken);
    }

    // ------------------------------------------------------- pause/reactivate

    [Fact]
    public async Task Pausing_blocks_the_athlete_and_kills_their_refresh_tokens()
    {
        var admin = await AdminClientAsync();
        var athlete = await SeedSignedInAthleteAsync("pause.target@nowhere.test", "Pause Target");

        var pause = await admin.PostAsync($"/api/v1/athletes/{athlete.UserId}/pause", null);
        pause.EnsureSuccessStatusCode();

        var body = await pause.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paused", body.GetProperty("status").GetString());

        // The refresh token must be dead, or the athlete simply renews past the pause.
        var refresh = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = athlete.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // ...and the still-unexpired access token stops working on the very next request.
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", athlete.AccessToken);
        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        Assert.Equal("ACCOUNT_PAUSED",
            (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        // ...and signing in again is refused too.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = "pause.target@nowhere.test", password = AthleteApiFactory.AthletePassword });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Reactivating_restores_login_without_issuing_tokens()
    {
        var admin = await AdminClientAsync();
        var athlete = await SeedSignedInAthleteAsync("reactivate.target@nowhere.test", "Reactivate Target");

        await admin.PostAsync($"/api/v1/athletes/{athlete.UserId}/pause", null);

        var reactivate = await admin.PostAsync($"/api/v1/athletes/{athlete.UserId}/reactivate", null);
        reactivate.EnsureSuccessStatusCode();

        var body = await reactivate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _), "reactivating must not issue tokens");

        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = "reactivate.target@nowhere.test", password = AthleteApiFactory.AthletePassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The old refresh token stays revoked — reactivation does not resurrect a session.
        var refresh = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = athlete.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Pausing_twice_is_safe()
    {
        var admin = await AdminClientAsync();
        var athlete = await SeedSignedInAthleteAsync("double.pause@nowhere.test", "Double Pause");

        await admin.PostAsync($"/api/v1/athletes/{athlete.UserId}/pause", null);
        var again = await admin.PostAsync($"/api/v1/athletes/{athlete.UserId}/pause", null);

        again.EnsureSuccessStatusCode();
        Assert.Equal("Paused",
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pausing_an_unknown_athlete_is_not_found()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsync($"/api/v1/athletes/{Guid.NewGuid()}/pause", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_cannot_pause_themselves_through_this_endpoint()
    {
        var admin = await AdminClientAsync();
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

        // The endpoint only ever matches users with the Athlete role.
        var response = await admin.PostAsync($"/api/v1/athletes/{me.GetProperty("id").GetGuid()}/pause", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --------------------------------------------------------- authorization

    [Fact]
    public async Task Every_athlete_endpoint_refuses_an_anonymous_caller()
    {
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/athletes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/athletes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/v1/athletes/{id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/v1/athletes/{id}/reactivate", null)).StatusCode);
    }

    [Fact]
    public async Task An_athlete_cannot_reach_the_admin_endpoints()
    {
        var athlete = await SeedSignedInAthleteAsync("nosy.athlete@nowhere.test", "Nosy Athlete");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", athlete.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/athletes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/v1/athletes/{athlete.UserId}/pause", null)).StatusCode);
    }

    // ----------------------------------------------------------- preferences

    [Fact]
    public async Task The_sort_preference_is_saved_server_side_and_returned_at_login()
    {
        var admin = await AdminClientAsync();

        var saved = await admin.PutAsJsonAsync("/api/v1/auth/me/preferences",
            new { athleteListSort = "NewestFirst" });
        saved.EnsureSuccessStatusCode();

        // On /auth/me, for restoring a session...
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("NewestFirst", me.GetProperty("athleteListSort").GetString());

        // ...and on a fresh login, so the app never has to ask twice.
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });
        var user = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");
        Assert.Equal("NewestFirst", user.GetProperty("athleteListSort").GetString());
    }

    [Fact]
    public async Task An_athlete_has_no_athlete_list_sort()
    {
        var athlete = await SeedSignedInAthleteAsync("sortless@nowhere.test", "Sortless Athlete");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", athlete.AccessToken);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

        // The preference belongs to the coach doing the sorting, never to the athlete sorted.
        Assert.Equal(JsonValueKind.Null, me.GetProperty("athleteListSort").ValueKind);
    }

    [Fact]
    public async Task An_unknown_sort_value_is_rejected_as_a_bad_request()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PutAsJsonAsync("/api/v1/auth/me/preferences",
            new { athleteListSort = "ByFavouriteColour" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A body that cannot be deserialised is the caller's fault, not a server fault.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("errorCode").GetString());
    }
}
