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
/// test. That also puts BR-07 under test, since an observation deducts exactly when the Admin
/// said it should when recording it.
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

    /// <summary>
    /// Closes whatever package this athlete currently holds, so a test that is about to buy one
    /// starts from a known balance. BR-03 allows only one active package, so any test that both
    /// purchases and shares an athlete with another test needs this — otherwise it passes or
    /// fails on the order xUnit happens to pick.
    /// </summary>
    private static async Task CloseActivePackageAsync(HttpClient admin, Guid athleteUserId)
    {
        var active = await admin.GetAsync($"/api/v1/athletes/{athleteUserId}/packages/active");

        if (active.StatusCode != HttpStatusCode.OK) return;

        var id = (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/packages/{id}/close", null)).EnsureSuccessStatusCode();
    }

    /// <param name="deductSession">
    /// Required, with no default on purpose: every test has to say what it expects the
    /// observation to do to the balance, rather than inheriting it from the duration.
    /// </param>
    private static async Task<Guid> ObservationAsync(
        HttpClient admin, Guid profileId, int minutes, bool deductSession)
    {
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(minutes),
            locationOrPlatform = "Regional final",
            deductSession
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

        await CloseActivePackageAsync(admin, athlete);
        var packageId = await PurchaseAsync(admin, athlete, "Attend – exactly once", 6);
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: true);

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
    public async Task An_observation_marked_not_to_deduct_is_attended_without_deducting()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "robin@nowhere.test");
        var profileId = await ProfileIdAsync("robin@nowhere.test");

        await CloseActivePackageAsync(admin, athlete);
        var packageId = await PurchaseAsync(admin, athlete, "Attend – non-deducting observation", 5);

        // 90 minutes: under the retired rule this would have deducted. The Admin's choice is
        // what decides now, so it does not.
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: false);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // BR-07: attended, and the balance untouched.
        Assert.Equal(0, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("Attended", body.GetProperty("session").GetProperty("status").GetString());
        Assert.False(body.GetProperty("session").GetProperty("observationDeductsSession").GetBoolean());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(5, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task A_short_observation_marked_to_deduct_consumes_one()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "robin@nowhere.test");
        var profileId = await ProfileIdAsync("robin@nowhere.test");

        await CloseActivePackageAsync(admin, athlete);
        var packageId = await PurchaseAsync(admin, athlete, "Attend – deducting short observation", 5);

        // 30 minutes: the mirror of the case above. The retired rule would have deducted nothing.
        var sessionId = await ObservationAsync(admin, profileId, minutes: 30, deductSession: true);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.True(body.GetProperty("session").GetProperty("observationDeductsSession").GetBoolean());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(1, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(4, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task A_no_show_uses_the_coachs_explicit_deduction_choice()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "nameless@nowhere.test");
        var profileId = await ProfileIdAsync("nameless@nowhere.test");

        var packageId = await PurchaseAsync(admin, athlete, "Attend – no-show", 3);
        var nonDeductingSessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: true);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{nonDeductingSessionId}/attend",
            new { outcome = "NoShow", deductSession = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("NoShow", body.GetProperty("session").GetProperty("status").GetString());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(3, package.GetProperty("remainingSessions").GetInt32());

        var repeated = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{nonDeductingSessionId}/attend",
            new { outcome = "NoShow", deductSession = true });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var repeatedProblem = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SESSION_ALREADY_RESOLVED", repeatedProblem.GetProperty("errorCode").GetString());

        // Recorded as non-deducting, then marked a no-show that does deduct: the no-show choice
        // is the one that applies, because the observation was never attended.
        var deductingSessionId = await ObservationAsync(admin, profileId, minutes: 45, deductSession: false);
        response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{deductingSessionId}/attend",
            new { outcome = "NoShow", deductSession = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("NoShow", body.GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("package").GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task An_observation_created_not_to_deduct_still_deducts_if_marked_a_no_show()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteUserIdAsync(admin, "sam@nowhere.test");
        var profileId = await ProfileIdAsync("sam@nowhere.test");

        await CloseActivePackageAsync(admin, athlete);
        var packageId = await PurchaseAsync(admin, athlete, "Attend – intent vs decision", 4);

        // The two deductSession fields answer different questions and neither overrides the
        // other. This one stores an intent that only ever applies to Attended...
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: false);

        var created = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}");
        Assert.False(created.GetProperty("observationDeductsSession").GetBoolean());

        // ...so a no-show never consults it, and the choice made at this moment is the one that
        // applies. The athlete did not turn up, so the observation was never attended and its
        // stored intent is simply not the question being answered.
        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend",
            new { outcome = "NoShow", deductSession = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal("NoShow", body.GetProperty("session").GetProperty("status").GetString());

        // The stored intent survives the no-show unchanged — it was never the deciding field.
        Assert.False(body.GetProperty("session").GetProperty("observationDeductsSession").GetBoolean());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(3, package.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task Deduct_session_is_required_only_for_no_show()
    {
        var admin = await AdminClientAsync();
        var arbitrarySession = Guid.NewGuid();

        var missing = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{arbitrarySession}/attend", new { outcome = "NoShow" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var presentForAttended = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{arbitrarySession}/attend",
            new { outcome = "Attended", deductSession = false });
        Assert.Equal(HttpStatusCode.BadRequest, presentForAttended.StatusCode);

        var nullForAttended = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{arbitrarySession}/attend",
            new { outcome = "Attended", deductSession = (bool?)null });
        Assert.Equal(HttpStatusCode.BadRequest, nullForAttended.StatusCode);

        var nullForNoShow = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{arbitrarySession}/attend",
            new { outcome = "NoShow", deductSession = (bool?)null });
        Assert.Equal(HttpStatusCode.BadRequest, nullForNoShow.StatusCode);

        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        var attendedProblem = await presentForAttended.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", missingProblem.GetProperty("errorCode").GetString());
        Assert.Equal("VALIDATION_FAILED", attendedProblem.GetProperty("errorCode").GetString());
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
        await CloseActivePackageAsync(admin, athlete);

        var sessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: true);

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_PACKAGE_NOT_FOUND", problem.GetProperty("errorCode").GetString());

        // A no-show with an explicit non-deduction does not require a package at all.
        var noShowSessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: true);
        var noShow = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{noShowSessionId}/attend",
            new { outcome = "NoShow", deductSession = false });

        Assert.Equal(HttpStatusCode.OK, noShow.StatusCode);
        var noShowBody = await noShow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, noShowBody.GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, noShowBody.GetProperty("package").ValueKind);
    }

    [Fact]
    public async Task An_athlete_cannot_mark_their_own_session_attended()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var sessionId = await ObservationAsync(admin, profileId, minutes: 90, deductSession: true);

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
        var sessionId = await ObservationAsync(admin, profileId, minutes: 75, deductSession: true);

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
        var sessionId = await ObservationAsync(admin, profileId, minutes: 75, deductSession: true);

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
            endUtc = start.AddMinutes(-30),
            deductSession = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_observation_without_a_deduction_choice_is_rejected()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        // Omitted entirely. There is no default: an unanswered question must not quietly
        // become "no".
        var omitted = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(90)
        });

        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        var problem = await omitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());

        // Explicitly null is the same answer as not answering.
        var explicitNull = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(90),
            deductSession = (bool?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, explicitNull.StatusCode);
    }

    [Fact]
    public async Task The_athlete_list_hands_the_app_the_id_that_creates_an_observation()
    {
        var admin = await AdminClientAsync();

        // The whole Phase 6E entry point, end to end: the app lists athletes, the coach picks
        // one, and the id off that row is posted straight to /sessions/observations. Reading
        // the profile id out of the database the way the other tests do would prove nothing
        // about what the list actually returns.
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes?search={Uri.EscapeDataString("jordan@nowhere.test")}");

        var row = page.GetProperty("items").EnumerateArray().Single();
        var profileId = row.GetProperty("athleteProfileId").GetGuid();
        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(90),
            locationOrPlatform = "Regional final",
            deductSession = true
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profileId, body.GetProperty("athleteProfileId").GetGuid());

        // Posting the user id instead is the mistake the second field exists to prevent, and it
        // must fail rather than quietly record an observation against nothing.
        var wrongId = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = row.GetProperty("id").GetGuid(),
            startUtc = start,
            endUtc = start.AddMinutes(90),
            deductSession = true
        });

        Assert.Equal(HttpStatusCode.NotFound, wrongId.StatusCode);
    }

    [Fact]
    public async Task An_observation_may_be_recorded_for_a_future_date()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("alex@nowhere.test");
        var athlete = await AthleteUserIdAsync(admin, "alex@nowhere.test");
        var start = DateTime.UtcNow.AddDays(7).Date.AddHours(8);
        start = DateTime.SpecifyKind(start, DateTimeKind.Utc);

        var before = await admin.GetAsync($"/api/v1/athletes/{athlete}/packages/active");
        var remainingBefore = before.StatusCode == HttpStatusCode.OK
            ? (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remainingSessions").GetInt32()
            : (int?)null;

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(90),
            locationOrPlatform = "Tournament venue",
            deductSession = true
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal("Observation", body.GetProperty("deliveryType").GetString());
        Assert.True(body.GetProperty("observationDeductsSession").GetBoolean());

        // Recording it changes no balance, however the choice was made (BR-04).
        var after = await admin.GetAsync($"/api/v1/athletes/{athlete}/packages/active");

        if (remainingBefore is { } expected)
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            Assert.Equal(expected,
                (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remainingSessions").GetInt32());
        }
    }

    [Fact]
    public async Task An_ordinary_session_reports_no_observation_choice()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync("jordan@nowhere.test");
        var sessionId = await ObservationAsync(admin, profileId, minutes: 60, deductSession: true);

        var observation = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}");
        Assert.True(observation.GetProperty("observationDeductsSession").GetBoolean());

        // The field is present on every session and null wherever BR-05 governs instead. No
        // Calendly-booked session exists in these tests to read, so the shape is asserted on the
        // one kind that can be created here; the null case is covered by the unit tests.
        Assert.Equal("Observation", observation.GetProperty("deliveryType").GetString());
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
            endUtc = start.AddMinutes(90),
            deductSession = true
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
