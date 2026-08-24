using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 6 end to end: purchase a package, record a session, mark it attended, and watch the
/// balance move exactly once.
/// <para>
/// Observations are what these tests use as their session, because they are the one kind this
/// API creates itself — every other session comes from Calendly, which is not reachable from a
/// test. That also puts BR-07 under test, since an observation deducts only when it ran longer
/// than an hour.
/// </para>
/// </summary>
public sealed class AttendanceTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<HttpClient> AthleteClientAsync(string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>
    /// Sessions and packages are keyed by athlete <em>profile</em> id, which no endpoint returns
    /// on its own, so it is read from the database the same way the athlete fixture writes it.
    /// </summary>
    private async Task<Guid> ProfileIdAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await (from user in db.Users
                      join profile in db.AthleteProfiles on user.Id equals profile.UserId
                      where user.Email == email
                      select profile.Id).SingleAsync();
    }

    private static async Task<Guid> AthleteUserIdAsync(HttpClient admin, string email)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes?search={Uri.EscapeDataString(email)}");

        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PurchaseAsync(
        HttpClient admin, Guid athleteUserId, string optionName, int sessions)
    {
        var option = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = optionName,
            sessions,
            defaultPriceMinor = 400_000L,
            features = new[] { "Weekly video call" }
        });

        if (option.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"option {(int)option.StatusCode}: {await option.Content.ReadAsStringAsync()}");

        var optionId = (await option.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var purchase = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteUserId}/packages", new { packageOptionId = optionId });

        if (purchase.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"purchase {(int)purchase.StatusCode}: {await purchase.Content.ReadAsStringAsync()}");

        return (await purchase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ObservationAsync(HttpClient admin, Guid profileId, int minutes)
    {
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(minutes),
            locationOrPlatform = "Regional final"
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"observation {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ------------------------------------------------------------------ purchase

    [Fact]
    public async Task A_purchase_starts_with_a_full_balance_at_the_price_the_catalogue_quoted()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "alex@nowhere.test");

        var packageId = await PurchaseAsync(admin, athlete, "Attend – full balance", 8);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(8, package.GetProperty("totalSessions").GetInt32());
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(8, package.GetProperty("remainingSessions").GetInt32());
        Assert.Equal(400_000, package.GetProperty("pricePaidMinor").GetInt64());
        Assert.Equal("Active", package.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_hold_two_active_packages()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "jordan@nowhere.test");

        await PurchaseAsync(admin, athlete, "Attend – BR-03 first", 4);

        var option = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = "Attend – BR-03 second",
            sessions = 4,
            defaultPriceMinor = 400_000L,
            features = new[] { "Weekly video call" }
        });
        var optionId = (await option.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var second = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athlete}/packages", new { packageOptionId = optionId });

        // BR-03.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_PACKAGE_EXISTS", problem.GetProperty("errorCode").GetString());
    }

    // ---------------------------------------------------------------- attendance

    [Fact]
    public async Task Marking_attended_deducts_exactly_one_and_a_second_attempt_deducts_nothing()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "sam@nowhere.test");
        var profileId = await ProfileIdAsync("sam@nowhere.test");

        var packageId = await PurchaseAsync(admin, athlete, "Attend – exactly once", 6);
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90);

        var first = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("Attended", body.GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(5, body.GetProperty("package").GetProperty("remainingSessions").GetInt32());
        Assert.Equal(1, body.GetProperty("progress").GetProperty("sessionNumber").GetInt32());

        var second = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SESSION_ALREADY_ATTENDED", problem.GetProperty("errorCode").GetString());

        // The balance is where the first call left it, which is the whole invariant.
        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(1, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(5, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task An_observation_of_an_hour_or_less_is_attended_without_deducting()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "robin@nowhere.test");
        var profileId = await ProfileIdAsync("robin@nowhere.test");

        var packageId = await PurchaseAsync(admin, athlete, "Attend – short observation", 5);
        var sessionId = await ObservationAsync(admin, profileId, minutes: 45);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // BR-07: attended, and the balance untouched.
        Assert.Equal(0, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("Attended", body.GetProperty("session").GetProperty("status").GetString());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(5, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task A_no_show_does_not_deduct_under_the_default_policy()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "nameless@nowhere.test");
        var profileId = await ProfileIdAsync("nameless@nowhere.test");

        var packageId = await PurchaseAsync(admin, athlete, "Attend – no-show", 3);
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "NoShow" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A-04.
        Assert.Equal(0, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("NoShow", body.GetProperty("session").GetProperty("status").GetString());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(3, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task A_session_that_would_deduct_is_refused_when_the_athlete_has_no_package()
    {
        var admin = await AdminClientAsync();

        // foreign@nowhere.test belongs to another coach, so use an athlete of this coach who has
        // deliberately never bought anything.
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var athlete = await AthleteUserIdAsync(admin, "alex@nowhere.test");

        // Close whatever this athlete holds, so the deduction has nowhere to go.
        var active = await admin.GetAsync($"/api/v1/athletes/{athlete}/packages/active");

        if (active.StatusCode == HttpStatusCode.OK)
        {
            var id = (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await admin.PostAsync($"/api/v1/packages/{id}/close", null)).EnsureSuccessStatusCode();
        }

        var sessionId = await ObservationAsync(admin, profileId, minutes: 90);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_PACKAGE_NOT_FOUND", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_mark_their_own_session_attended()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90);

        var athlete = await AthleteClientAsync("alex@nowhere.test");

        var response = await athlete.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --------------------------------------------------------------------- notes

    [Fact]
    public async Task Notes_are_added_edited_listed_and_deleted()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("jordan@nowhere.test");
        var sessionId = await ObservationAsync(admin, profileId, minutes: 75);

        var created = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes", new { content = "Held her line under pressure." });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = await created.Content.ReadFromJsonAsync<JsonElement>();
        var noteId = note.GetProperty("id").GetGuid();

        var edited = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}", new { content = "Held her line under real pressure." });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var updated = await edited.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Held her line under real pressure.", updated.GetProperty("content").GetString());

        // Editing keeps the note where it sits in the history. Compared with a tolerance
        // because the created response carries the in-memory value at full .NET precision while
        // the edited one has been round-tripped through Postgres, which stores microseconds - a
        // difference of a few hundred nanoseconds that says nothing about the behaviour.
        Assert.True(
            (note.GetProperty("createdAtUtc").GetDateTime()
             - updated.GetProperty("createdAtUtc").GetDateTime()).Duration() < TimeSpan.FromMilliseconds(1),
            "Editing a note must not move its createdAtUtc.");

        Assert.True(updated.GetProperty("updatedAtUtc").GetDateTime()
                    > updated.GetProperty("createdAtUtc").GetDateTime());

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
        Assert.Single(list.EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}")).StatusCode);
    }

    [Fact]
    public async Task An_athlete_cannot_read_the_coachs_session_notes()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var sessionId = await ObservationAsync(admin, profileId, minutes: 75);

        var athlete = await AthleteClientAsync("alex@nowhere.test");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync($"/api/v1/sessions/{sessionId}/notes")).StatusCode);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public async Task An_observation_that_ends_before_it_starts_is_rejected()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(-30)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_observation_for_another_coachs_athlete_is_not_found()
    {
        var admin = await AdminClientAsync();
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = Guid.NewGuid(),
            startUtc = start,
            endUtc = start.AddMinutes(90)
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
