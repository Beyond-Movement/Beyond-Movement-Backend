using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The Observation Request flow end to end: the athlete asks, edits or withdraws while the coach
/// has not answered; the coach adjusts, accepts or declines; and an acceptance produces exactly
/// one Observation session and moves no balance.
/// <para>
/// The two things worth holding on to across every test here are that <b>Pending is the only
/// state anything may be done from</b>, and that <b>accepting creates a session but consumes
/// nothing</b> — a booking never deducts (BR-04), and the Admin's BR-07 choice is stored for
/// Mark as Attended rather than applied now.
/// </para>
/// <para>
/// <b>Asking requires an active package that includes observations</b>, which is why almost every
/// test here signs in through <see cref="EligibleAthleteClientAsync"/> rather than plain
/// <c>AthleteClientAsync</c>. The eligibility rule itself — and everything it deliberately does
/// not apply to — is in the section at the end.
/// </para>
/// </summary>
public sealed class ObservationRequestTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Athlete = "alex@nowhere.test";
    private const string OtherAthlete = "jordan@nowhere.test";

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    // ----------------------------------------------------------------- athlete

    [Fact]
    public async Task An_athlete_can_ask_to_be_observed()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var start = Future(days: 10);

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = start,
            location = "Cairo International Stadium",
            details = "National final - I would like you to watch my starts."
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(start, body.GetProperty("requestedStartUtc").GetDateTime());
        Assert.Equal("Cairo International Stadium", body.GetProperty("location").GetString());

        // The athlete's form does not ask for a duration, so the request carries the default and
        // an end that follows from it - a Pending request the Admin can already draw as a block.
        Assert.Equal(60, body.GetProperty("requestedDurationMinutes").GetInt32());
        Assert.Equal(start.AddMinutes(60), body.GetProperty("requestedEndUtc").GetDateTime());

        // Nothing exists yet but the request itself.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sessionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("resolvedAtUtc").ValueKind);

        // Labelled for the Admin's queue without a second call.
        Assert.Equal("Alex Thompson", body.GetProperty("athleteName").GetString());
    }

    [Fact]
    public async Task Details_are_optional()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 11),
            location = "Maadi Club"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task A_request_in_the_past_is_refused()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = DateTime.UtcNow.AddDays(-1),
            location = "Cairo International Stadium"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_request_with_no_location_is_refused()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 12),
            location = "  "
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_athlete_can_edit_a_pending_request()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 13), "Maadi Club", "First guess");
        var moved = Future(days: 14);

        var response = await athlete.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = moved,
            location = "Alexandria Sports Hall",
            details = "Moved to the regional heats",
            requestedDurationMinutes = 120
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(moved, body.GetProperty("requestedStartUtc").GetDateTime());
        Assert.Equal("Alexandria Sports Hall", body.GetProperty("location").GetString());
        Assert.Equal("Moved to the regional heats", body.GetProperty("details").GetString());
        Assert.Equal(120, body.GetProperty("requestedDurationMinutes").GetInt32());

        // Editing is not deciding: the coach still has it.
        Assert.Equal("Pending", body.GetProperty("status").GetString());
    }

    /// <summary>
    /// The edit is a full replacement, like the profile and purchase endpoints. Worth its own
    /// test because it is the rule a client is most likely to get wrong, and the one that
    /// silently loses what the athlete wrote.
    /// </summary>
    [Fact]
    public async Task Editing_without_details_clears_them()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 15), "Maadi Club", "Something to say");

        var response = await athlete.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = Future(days: 15),
            location = "Maadi Club"
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("details").ValueKind);
        Assert.Equal(60, body.GetProperty("requestedDurationMinutes").GetInt32());
    }

    [Fact]
    public async Task An_athlete_can_cancel_a_pending_request()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 16), "Maadi Club");

        var response = await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("resolvedAtUtc").ValueKind);

        // Cancelled means no session, now or ever.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sessionId").ValueKind);
        Assert.Equal(0, await SessionCountAsync(id));
    }

    [Fact]
    public async Task Cancelling_twice_is_a_conflict_rather_than_a_silent_success()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 17), "Maadi Club");

        (await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null))
            .EnsureSuccessStatusCode();

        var repeat = await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        await AssertErrorAsync(repeat, "OBSERVATION_REQUEST_NOT_PENDING");
    }

    [Fact]
    public async Task An_athlete_cannot_edit_a_request_after_it_is_accepted()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 18), "Maadi Club");

        await AcceptAsync(admin, id, deductSession: false);

        var response = await athlete.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = Future(days: 19),
            location = "Somewhere else"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATION_REQUEST_NOT_PENDING");

        // Refused, not partially applied.
        var after = await athlete.GetFromJsonAsync<JsonElement>($"/api/v1/me/observation-requests/{id}");
        Assert.Equal("Maadi Club", after.GetProperty("location").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_cancel_a_request_after_it_is_accepted()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 20), "Maadi Club");

        await AcceptAsync(admin, id, deductSession: false);

        // By now there is a real session, and cancelling that is POST /sessions/{id}/cancel.
        var response = await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATION_REQUEST_NOT_PENDING");
    }

    [Fact]
    public async Task An_athlete_cannot_edit_a_request_after_it_is_declined()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 21), "Maadi Club");

        (await admin.PostAsync($"/api/v1/observation-requests/{id}/decline", null))
            .EnsureSuccessStatusCode();

        var response = await athlete.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = Future(days: 22),
            location = "Let me try again"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATION_REQUEST_NOT_PENDING");
    }

    /// <summary>
    /// Another athlete's request is 404, not 403 — the same rule every other resource in this
    /// API follows, so an id cannot be probed for existence.
    /// </summary>
    [Fact]
    public async Task Another_athlete_can_neither_read_nor_change_the_request()
    {
        var owner = await EligibleAthleteClientAsync(Athlete);
        var stranger = await EligibleAthleteClientAsync(OtherAthlete);
        var id = await RequestAsync(owner, Future(days: 23), "Maadi Club");

        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/v1/me/observation-requests/{id}")).StatusCode);

        var edit = await stranger.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = Future(days: 24),
            location = "Not mine to move"
        });

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        await AssertErrorAsync(edit, "OBSERVATION_REQUEST_NOT_FOUND");

        var cancel = await stranger.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);

        // Untouched, and still the owner's.
        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/me/observation-requests/{id}");
        Assert.Equal("Pending", after.GetProperty("status").GetString());
        Assert.Equal("Maadi Club", after.GetProperty("location").GetString());
    }

    [Fact]
    public async Task An_athlete_sees_only_their_own_requests()
    {
        var owner = await EligibleAthleteClientAsync(Athlete);
        var stranger = await EligibleAthleteClientAsync(OtherAthlete);
        var id = await RequestAsync(owner, Future(days: 25), "Maadi Club");

        var mine = await stranger.GetFromJsonAsync<JsonElement>("/api/v1/me/observation-requests");
        var ids = mine.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToArray();

        Assert.DoesNotContain(id, ids);
    }

    // ------------------------------------------------------------------- admin

    [Fact]
    public async Task An_admin_cannot_use_the_athlete_routes()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 26),
            location = "Not the coach's to ask for"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_athlete_cannot_use_the_admin_routes()
    {
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 27), "Maadi Club");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync("/api/v1/observation-requests")).StatusCode);

        var accept = await athlete.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession = false });

        // The athlete cannot accept their own request into existence.
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Equal(0, await SessionCountAsync(id));
    }

    [Fact]
    public async Task The_admin_queue_carries_the_athlete_on_every_row()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 28), "Maadi Club");

        var page = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/observation-requests?status=Pending&pageSize=100");

        var row = page.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == id);

        Assert.Equal("Alex Thompson", row.GetProperty("athleteName").GetString());

        // The USER id, which is what GET /athletes/{athleteId} takes - not the profile id.
        Assert.Equal(await AthleteUserIdAsync(admin, Athlete), row.GetProperty("athleteUserId").GetGuid());
    }

    [Fact]
    public async Task An_unknown_athlete_filter_is_a_404_rather_than_an_empty_page()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/observation-requests?athleteId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "ATHLETE_NOT_FOUND");
    }

    [Fact]
    public async Task An_admin_can_adjust_a_request_before_answering_it()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 29), "Maadi Club", "Watch my starts");
        var moved = Future(days: 30);

        var response = await admin.PutAsJsonAsync($"/api/v1/observation-requests/{id}", new
        {
            requestedStartUtc = moved,
            location = "Cairo International Stadium",
            details = "Watch my starts",
            requestedDurationMinutes = 90
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(moved, body.GetProperty("requestedStartUtc").GetDateTime());
        Assert.Equal("Cairo International Stadium", body.GetProperty("location").GetString());
        Assert.Equal(90, body.GetProperty("requestedDurationMinutes").GetInt32());

        // Adjusting is not answering: still waiting, and still no session.
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(0, await SessionCountAsync(id));

        // And the athlete sees what the coach proposed.
        var seen = await athlete.GetFromJsonAsync<JsonElement>($"/api/v1/me/observation-requests/{id}");
        Assert.Equal("Cairo International Stadium", seen.GetProperty("location").GetString());
    }

    [Fact]
    public async Task Accepting_creates_one_observation_session_from_the_accepted_values()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var start = Future(days: 31);
        var id = await RequestAsync(athlete, start, "Maadi Club", "Watch my turns");

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var request = body.GetProperty("request");
        var session = body.GetProperty("session");

        Assert.Equal("Accepted", request.GetProperty("status").GetString());
        var sessionId = request.GetProperty("sessionId").GetGuid();
        Assert.Equal(sessionId, session.GetProperty("id").GetGuid());

        // The accepted values became the session's scheduled details.
        Assert.Equal("Observation", session.GetProperty("deliveryType").GetString());
        Assert.Equal("Scheduled", session.GetProperty("status").GetString());
        Assert.Equal(start, session.GetProperty("startUtc").GetDateTime());
        Assert.Equal(start.AddMinutes(60), session.GetProperty("endUtc").GetDateTime());
        Assert.Equal(60, session.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Maadi Club", session.GetProperty("locationOrPlatform").GetString());

        // BR-07 stored for Mark as Attended, not applied now.
        Assert.True(session.GetProperty("observationDeductsSession").GetBoolean());

        // Calendly was never involved, so there is no meeting or reschedule link.
        Assert.Equal(JsonValueKind.Null, session.GetProperty("meetingUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("rescheduleUrl").ValueKind);

        // Exactly one, and it is readable through the ordinary session endpoints.
        Assert.Equal(1, await SessionCountAsync(id));
        (await admin.GetAsync($"/api/v1/sessions/{sessionId}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Accepting_applies_the_admins_changes_to_both_the_session_and_the_request()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 32), "Maadi Club", "First guess");
        var moved = Future(days: 33);

        var response = await admin.PostAsJsonAsync($"/api/v1/observation-requests/{id}/accept", new
        {
            deductSession = false,
            requestedStartUtc = moved,
            location = "Cairo International Stadium",
            requestedDurationMinutes = 120
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var session = body.GetProperty("session");
        Assert.Equal(moved, session.GetProperty("startUtc").GetDateTime());
        Assert.Equal(120, session.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Cairo International Stadium", session.GetProperty("locationOrPlatform").GetString());
        Assert.False(session.GetProperty("observationDeductsSession").GetBoolean());

        // The request records what was agreed, not what was originally proposed - so the athlete
        // reading it back sees the same observation the coach put on the schedule.
        var request = body.GetProperty("request");
        Assert.Equal(moved, request.GetProperty("requestedStartUtc").GetDateTime());
        Assert.Equal("Cairo International Stadium", request.GetProperty("location").GetString());

        // An override left out keeps what the athlete asked for.
        Assert.Equal("First guess", request.GetProperty("details").GetString());
    }

    [Fact]
    public async Task Accepting_without_the_deduction_choice_is_refused()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 34), "Maadi Club");

        // BR-07 is the Admin's explicit choice and has no default, so an accept that does not
        // make it creates nothing.
        var response = await admin.PostAsJsonAsync($"/api/v1/observation-requests/{id}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await SessionCountAsync(id));
    }

    /// <summary>
    /// The guarantee the whole acceptance path exists to keep: however many times Accept is
    /// called, one session.
    /// </summary>
    [Fact]
    public async Task A_repeated_accept_is_a_conflict_and_never_a_second_session()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 35), "Maadi Club");

        var first = await AcceptAsync(admin, id, deductSession: false);

        var repeat = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession = true });

        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        await AssertErrorAsync(repeat, "OBSERVATION_REQUEST_NOT_PENDING");

        Assert.Equal(1, await SessionCountAsync(id));

        // The first decision stands, including its deduction choice.
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/observation-requests/{id}");
        Assert.Equal(first, after.GetProperty("sessionId").GetGuid());

        var session = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{first}");
        Assert.False(session.GetProperty("observationDeductsSession").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_accepts_produce_one_session_and_one_conflict()
    {
        var admin = await AdminClientAsync();
        var second = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 36), "Maadi Club");

        var body = new { deductSession = false };

        var responses = await Task.WhenAll(
            admin.PostAsJsonAsync($"/api/v1/observation-requests/{id}/accept", body),
            second.PostAsJsonAsync($"/api/v1/observation-requests/{id}/accept", body));

        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await SessionCountAsync(id));
    }

    [Fact]
    public async Task Declining_creates_no_session()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 37), "Maadi Club");

        var response = await admin.PostAsync($"/api/v1/observation-requests/{id}/decline", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Declined", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sessionId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("resolvedAtUtc").ValueKind);
        Assert.Equal(0, await SessionCountAsync(id));
    }

    [Fact]
    public async Task A_declined_request_cannot_then_be_accepted()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 38), "Maadi Club");

        (await admin.PostAsync($"/api/v1/observation-requests/{id}/decline", null))
            .EnsureSuccessStatusCode();

        var accept = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession = false });

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        Assert.Equal(0, await SessionCountAsync(id));
    }

    [Fact]
    public async Task A_cancelled_request_cannot_then_be_accepted()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var id = await RequestAsync(athlete, Future(days: 39), "Maadi Club");

        (await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null))
            .EnsureSuccessStatusCode();

        var accept = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession = false });

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        await AssertErrorAsync(accept, "OBSERVATION_REQUEST_NOT_PENDING");
        Assert.Equal(0, await SessionCountAsync(id));
    }

    [Fact]
    public async Task Another_coachs_request_is_not_found_rather_than_forbidden()
    {
        var admin = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/v1/observation-requests/{Guid.NewGuid()}")).StatusCode);

        var accept = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{Guid.NewGuid()}/accept", new { deductSession = false });

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        await AssertErrorAsync(accept, "OBSERVATION_REQUEST_NOT_FOUND");
    }

    // ----------------------------------------------------------------- balance

    /// <summary>
    /// BR-04, on the path that did not exist before: asking is free and so is accepting. The
    /// balance moves at Mark as Attended and nowhere else, which is what the last leg checks —
    /// so this is not "the deduction never happens" but "it happens later, once".
    /// </summary>
    [Fact]
    public async Task Neither_requesting_nor_accepting_moves_the_package_balance()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);

        await CloseActivePackageAsync(admin, athleteUserId);
        var packageId = await PurchaseAsync(admin, athleteUserId, "Observation request pack", sessions: 4);
        Assert.Equal(4, await RemainingAsync(admin, packageId));

        // The athlete asks: nothing moves.
        var start = DateTime.UtcNow.AddMinutes(2);
        var id = await RequestAsync(athlete, start, "Maadi Club");
        Assert.Equal(4, await RemainingAsync(admin, packageId));

        // The coach accepts, choosing that it will deduct: still nothing moves, because a
        // scheduled session has never taken anything.
        var sessionId = await AcceptAsync(admin, id, deductSession: true);
        Assert.Equal(4, await RemainingAsync(admin, packageId));

        var session = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}");
        Assert.Equal("Scheduled", session.GetProperty("status").GetString());

        // And only now, once it has actually happened, does the choice apply.
        await WaitUntilStartedAsync(start);
        var attend = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        attend.EnsureSuccessStatusCode();
        Assert.Equal(1, (await attend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("consumedSessionCount").GetInt32());
        Assert.Equal(3, await RemainingAsync(admin, packageId));
    }

    /// <summary>
    /// Accepting does not require the athlete to have a package, exactly as
    /// <c>POST /sessions/observations</c> does not. Only ASKING needs one, so the package is closed
    /// between the ask and the acceptance: the coach agreed to be somewhere, and an expired package
    /// is not a reason to withdraw that.
    /// </summary>
    [Fact]
    public async Task Accepting_does_not_require_the_athlete_to_still_have_a_package()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        var id = await RequestAsync(athlete, Future(days: 40), "Maadi Club");

        await CloseActivePackageAsync(admin, athleteUserId);

        var sessionId = await AcceptAsync(admin, id, deductSession: true);

        Assert.Equal(1, await SessionCountAsync(id));
        (await admin.GetAsync($"/api/v1/sessions/{sessionId}")).EnsureSuccessStatusCode();
    }

    // ------------------------------------------------------------- eligibility

    /// <summary>
    /// The rule: an athlete may ask only while their active package includes the Observations
    /// feature. Read from the package's snapshot, never from the catalogue and never from the
    /// display text of a feature.
    /// </summary>
    [Fact]
    public async Task An_athlete_whose_package_includes_observations_may_ask()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(Athlete);
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);

        // The same fact the app reads to decide whether to show the action at all.
        var package = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteUserId}/packages/active");

        Assert.Contains(
            Features.Observations,
            package.GetProperty("includedFeatures").EnumerateArray().Select(f => f.GetString()));

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 41),
            location = "Cairo International Stadium"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task An_athlete_with_no_active_package_cannot_ask()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        await CloseActivePackageAsync(admin, athleteUserId);

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 42),
            location = "Maadi Club"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATIONS_NOT_INCLUDED");
    }

    /// <summary>
    /// An active package that was not sold with observations is the same answer as no package at
    /// all — one code, because the athlete's next step is the same either way.
    /// </summary>
    [Fact]
    public async Task An_athlete_whose_package_omits_observations_cannot_ask()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        await CloseActivePackageAsync(admin, athleteUserId);
        await SellAsync(admin, athleteUserId, "No observations pack", Features.Open("Weekly video call"));

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 43),
            location = "Maadi Club"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATIONS_NOT_INCLUDED");
    }

    /// <summary>
    /// The point of the code. A package whose feature line READS "Observations" but carries no code
    /// grants nothing — otherwise the rule would be decided by display text the coach is free to
    /// reword, and a package in Arabic would behave differently from the same package in English.
    /// </summary>
    [Fact]
    public async Task A_feature_that_only_says_observations_does_not_make_an_athlete_eligible()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        await CloseActivePackageAsync(admin, athleteUserId);
        await SellAsync(admin, athleteUserId, "Says observations pack", Features.Open("Observations"));

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 44),
            location = "Maadi Club"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorAsync(response, "OBSERVATIONS_NOT_INCLUDED");
    }

    /// <summary>
    /// Conversely, the coach may word the line however they like. The code is what counts, so a
    /// package that never says the word "observations" anywhere still grants them.
    /// </summary>
    [Fact]
    public async Task Any_wording_works_as_long_as_the_code_is_there()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        await CloseActivePackageAsync(admin, athleteUserId);
        await SellAsync(admin, athleteUserId, "Oddly worded pack",
            Features.List(Features.One("I come and watch you compete", Features.Observations)));

        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 45),
            location = "Maadi Club"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// A request that already exists stays the athlete's to edit and withdraw even after their
    /// package stops including observations. The asking already happened; a coach who no longer
    /// wants to go declines, and freezing the athlete out of their own pending request would leave
    /// them unable to correct a wrong address for a session the coach may still attend.
    /// </summary>
    [Fact]
    public async Task A_pending_request_stays_editable_after_eligibility_lapses()
    {
        var admin = await AdminClientAsync();
        var athlete = await EligibleAthleteClientAsync(OtherAthlete);
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);

        var id = await RequestAsync(athlete, Future(days: 46), "Maadi Club", "First guess");

        await CloseActivePackageAsync(admin, athleteUserId);

        // Asking again is refused...
        var blocked = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = Future(days: 47),
            location = "Maadi Club"
        });
        await AssertErrorAsync(blocked, "OBSERVATIONS_NOT_INCLUDED");

        // ...while the request already filed is still fully theirs.
        var edited = await athlete.PutAsJsonAsync($"/api/v1/me/observation-requests/{id}", new
        {
            requestedStartUtc = Future(days: 48),
            location = "Cairo International Stadium",
            details = "Corrected the venue"
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Cairo International Stadium",
            (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("location").GetString());

        var cancelled = await athlete.PostAsync($"/api/v1/me/observation-requests/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled",
            (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    /// <summary>
    /// A-03 is untouched. The Admin recording an observation directly needs no package and no
    /// feature — the coach decides what the coach observes, and the package rule is about what the
    /// athlete may ASK for.
    /// </summary>
    [Fact]
    public async Task An_admin_can_record_an_observation_for_an_athlete_with_no_package()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);
        var athleteProfileId = await AthleteProfileIdAsync(athleteUserId);

        await CloseActivePackageAsync(admin, athleteUserId);

        // An observation is recorded after it happened, so the range is in the past.
        var start = DateTime.UtcNow.AddDays(-2);

        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId,
            startUtc = start,
            endUtc = start.AddHours(1),
            locationOrPlatform = "Cairo International Stadium",
            deductSession = false
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// A start far enough ahead that the future-date rule is comfortably satisfied. Spread per
    /// test so two tests in the same class cannot collide on one athlete's schedule.
    /// </summary>
    private static DateTime Future(int days) =>
        new DateTime(DateTime.UtcNow.Ticks, DateTimeKind.Utc).Date.AddDays(days).AddHours(10);

    private async Task<HttpClient> AdminClientAsync() =>
        await ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteClientAsync(string email) =>
        await ClientAsync(email, AthleteApiFactory.AthletePassword);

    /// <summary>
    /// An athlete signed in and able to ask to be observed — which now means holding an active
    /// package that includes the Observations feature.
    /// <para>
    /// Idempotent, because the tests in this class share one database and one athlete: an athlete
    /// who is already eligible is left alone, and one holding a package that does not include
    /// observations has it closed first, since BR-03 allows only one active package at a time.
    /// </para>
    /// </summary>
    private async Task<HttpClient> EligibleAthleteClientAsync(string email)
    {
        var athlete = await AthleteClientAsync(email);
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, email);

        var active = await admin.GetAsync($"/api/v1/athletes/{athleteUserId}/packages/active");

        if (active.StatusCode == HttpStatusCode.OK)
        {
            var included = (await active.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("includedFeatures").EnumerateArray()
                .Select(f => f.GetString()).ToArray();

            if (included.Contains(Features.Observations)) return athlete;

            await CloseActivePackageAsync(admin, athleteUserId);
        }

        await SellAsync(admin, athleteUserId, ObservationsOptionName, Features.WithObservations());
        return athlete;
    }

    private const string ObservationsOptionName = "Observation requests included";

    /// <summary>
    /// Sells the athlete a package with the given features, reusing the catalogue option when one
    /// of that name already exists — option names are unique per coach, so a helper called by
    /// every test in the class cannot create a fresh one each time.
    /// </summary>
    private static async Task SellAsync(
        HttpClient admin, Guid athleteUserId, string optionName, object[] features)
    {
        var optionId = await OptionIdAsync(admin, optionName, features);

        var purchase = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteUserId}/packages", new { packageOptionId = optionId });

        if (purchase.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"sell {(int)purchase.StatusCode}: {await purchase.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> OptionIdAsync(
        HttpClient admin, string optionName, object[] features)
    {
        var existing = await admin.GetFromJsonAsync<JsonElement>("/api/v1/package-options");

        foreach (var option in existing.EnumerateArray())
        {
            if (option.GetProperty("name").GetString() == optionName)
                return option.GetProperty("id").GetGuid();
        }

        var created = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = optionName,
            sessions = 20,
            defaultPriceMinor = 400_000L,
            features
        });

        if (created.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"option {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> ClientAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<Guid> RequestAsync(
        HttpClient athlete, DateTime startUtc, string location, string? details = null)
    {
        var response = await athlete.PostAsJsonAsync("/api/v1/me/observation-requests", new
        {
            requestedStartUtc = startUtc,
            location,
            details
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"request {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Accepts, and returns the id of the session it created.</summary>
    private static async Task<Guid> AcceptAsync(HttpClient admin, Guid id, bool deductSession)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/observation-requests/{id}/accept", new { deductSession });

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"accept {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// How many sessions this request has produced, read from the database rather than from an
    /// endpoint: the assertion is that a second one does not exist, and no API can show what was
    /// never created.
    /// </summary>
    private async Task<int> SessionCountAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ObservationRequests.AsNoTracking()
            .Where(x => x.Id == requestId && x.SessionId != null)
            .CountAsync();
    }

    /// <summary>
    /// Requests and sessions are keyed by the profile id, while every /athletes route takes the
    /// user id. Read from the database because no endpoint returns the profile id on its own.
    /// </summary>
    private async Task<Guid> AthleteProfileIdAsync(Guid athleteUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == athleteUserId)
            .Select(x => x.Id)
            .SingleAsync();
    }

    private static async Task<Guid> AthleteUserIdAsync(HttpClient admin, string email)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes?search={Uri.EscapeDataString(email)}");

        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static async Task<int> RemainingAsync(HttpClient admin, Guid packageId) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}"))
        .GetProperty("remainingSessions").GetInt32();

    private static async Task<Guid> PurchaseAsync(
        HttpClient admin, Guid athleteUserId, string optionName, int sessions)
    {
        var option = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = optionName,
            sessions,
            defaultPriceMinor = 400_000L,
            features = Features.WithObservations()
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

    private static async Task CloseActivePackageAsync(HttpClient admin, Guid athleteUserId)
    {
        var active = await admin.GetAsync($"/api/v1/athletes/{athleteUserId}/packages/active");

        if (active.StatusCode != HttpStatusCode.OK) return;

        var id = (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/packages/{id}/close", null)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Attendance cannot be recorded before the scheduled start, so a test that needs to attend
    /// a session it just created has to let that moment arrive. The request itself must be in
    /// the future, so the two rules leave a short wait as the only way through.
    /// </summary>
    private static async Task WaitUntilStartedAsync(DateTime startUtc)
    {
        var remaining = startUtc - DateTime.UtcNow;

        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining + TimeSpan.FromSeconds(1));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string errorCode) =>
        Assert.Equal(errorCode, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString());
}
