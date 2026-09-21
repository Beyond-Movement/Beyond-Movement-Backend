using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests count the sessions one package was spent on, so nothing else may
/// be attending sessions for the same athletes.
/// </summary>
public sealed class PackageSessionHistoryApiFactory : PurchaseApiFactory
{
    /// <summary>
    /// An ordinary Calendly-booked session, written straight to the database.
    /// <para>
    /// Observations are the only sessions this API creates itself, so a test cannot book an
    /// Online one through the API — Calendly is not reachable from here. It is inserted the same
    /// way the projection would: through <see cref="Session.Create"/>, so the row is exactly what
    /// a real booking produces. Without it every test session would be an observation, and BR-05
    /// — an ordinary attended session consumes one — would never be exercised end to end.
    /// </para>
    /// </summary>
    public async Task<Guid> AddBookedSessionAsync(
        Guid athleteProfileId, DateTime startUtc, int minutes = 60)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var coachId = (await db.Users.AsNoTracking().SingleAsync(u => u.Role == UserRole.Admin)).Id;

        // Unique per call: the Calendly identifiers carry unique indexes, and two sessions in one
        // test would otherwise collide.
        var token = Guid.NewGuid();

        var session = Session.Create(coachId, athleteProfileId, new CalendlySessionData(
            $"https://api.calendly.com/scheduled_events/{token}",
            $"https://api.calendly.com/invitees/{token}",
            "https://api.calendly.com/event_types/coaching",
            startUtc, startUtc.AddMinutes(minutes),
            DeliveryType.Online, "Zoom", "https://zoom.test/meeting",
            "https://calendly.test/cancel", "https://calendly.test/reschedule"), startUtc);

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return session.Id;
    }
}

/// <summary>
/// The sessions a purchased package was spent on — <c>GET /packages/{packageId}/sessions</c> and
/// the athlete's own <c>GET /me/packages/{packageId}/sessions</c>.
/// <para>
/// The rule the whole endpoint rests on is that <b>membership is consumption</b>: a session is in
/// the list exactly when it took one off this package. These pin both halves of that — every kind
/// of session that deducts is present, every kind that does not is absent — and then pin the
/// consequence that makes it trustworthy, which is that the number of rows is the package's
/// <c>usedSessions</c>.
/// </para>
/// </summary>
public sealed class PackageSessionHistoryTests(PackageSessionHistoryApiFactory factory)
    : IClassFixture<PackageSessionHistoryApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Well in the past, so a session can be resolved: the application runs on the real clock and
    /// refuses to mark a future session attended.
    /// </summary>
    private static readonly DateTime Past = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    // --- clients -------------------------------------------------------------

    private async Task<HttpClient> AdminClientAsync() =>
        await ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteClientAsync(string email) =>
        await ClientAsync(email, AthleteApiFactory.AthletePassword);

    private async Task<HttpClient> ClientAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    // --- arrangement ---------------------------------------------------------

    /// <summary>
    /// Sessions and packages are keyed by athlete <em>profile</em> id, which no endpoint returns
    /// on its own, so it is read from the database the same way the fixture writes it.
    /// </summary>
    private Task<Guid> ProfileIdAsync(Guid athleteUserId) =>
        factory.QueryAsync(db => db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == athleteUserId)
            .Select(x => x.Id)
            .SingleAsync());

    private static async Task<Guid> PurchaseAsync(
        HttpClient admin, Guid athleteUserId, string optionName, int sessions = 8)
    {
        var option = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = optionName,
            sessions,
            defaultPriceMinor = 400_000L,
            features = Features.Open("Weekly video call")
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

    /// <param name="deductSession">
    /// Required, with no default on purpose: every test has to say what it expects the
    /// observation to do to the balance rather than inheriting it.
    /// </param>
    private static async Task<Guid> ObservationAsync(
        HttpClient admin, Guid profileId, bool deductSession, DateTime startUtc, int minutes = 60)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc,
            endUtc = startUtc.AddMinutes(minutes),
            locationOrPlatform = "Regional final",
            deductSession
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"observation {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Marks a session attended. <paramref name="deductSession"/> applies to a no-show only.</summary>
    private static async Task AttendAsync(
        HttpClient admin, Guid sessionId, string outcome = "Attended", bool? deductSession = null)
    {
        var body = deductSession is null
            ? (object)new { outcome }
            : new { outcome, deductSession };

        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/attend", body);

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"attend {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task CancelAsync(HttpClient admin, Guid sessionId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/cancel", new { reason = "Changed plans" });

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"cancel {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // --- reads ---------------------------------------------------------------

    private static async Task<JsonElement> SessionsAsync(
        HttpClient admin, Guid packageId, string query = "")
    {
        var response = await admin.GetAsync(
            $"/api/v1/packages/{packageId}/sessions{(query.Length == 0 ? "" : "?" + query)}");

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"sessions {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> MySessionsAsync(
        HttpClient athlete, Guid packageId, string query = "")
    {
        var response = await athlete.GetAsync(
            $"/api/v1/me/packages/{packageId}/sessions{(query.Length == 0 ? "" : "?" + query)}");

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"my sessions {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid[] IdsOf(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())];

    // --- what belongs in the list --------------------------------------------

    [Fact]
    public async Task An_attended_session_that_deducted_is_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – attended");

        // An ordinary Calendly-booked session, so BR-05 rather than BR-07 decides.
        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId);

        var page = await SessionsAsync(admin, packageId);

        Assert.Equal([sessionId], IdsOf(page));

        var row = page.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Attended", row.GetProperty("status").GetString());
        Assert.Equal("Online", row.GetProperty("deliveryType").GetString());
        Assert.Equal(1, row.GetProperty("consumedPackagePosition").GetInt32());
        Assert.Equal(60, row.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Zoom", row.GetProperty("locationOrPlatform").GetString());

        // Attended, so the stamp is there. A no-show's is null - see the test below.
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("attendedAtUtc").ValueKind);
    }

    [Fact]
    public async Task A_no_show_the_coach_charged_is_in_the_list_and_carries_no_attendance_stamp()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – charged no-show");

        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId, "NoShow", deductSession: true);

        var row = (await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray().Single();

        // It is here because the coach chose to charge it. That is what makes it part of what
        // the package was spent on, even though nobody turned up.
        Assert.Equal(sessionId, row.GetProperty("id").GetGuid());
        Assert.Equal("NoShow", row.GetProperty("status").GetString());
        Assert.Equal(1, row.GetProperty("consumedPackagePosition").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("attendedAtUtc").ValueKind);
    }

    [Fact]
    public async Task An_observation_that_deducted_is_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – observation");

        var sessionId = await ObservationAsync(admin, profileId, deductSession: true, Past);
        await AttendAsync(admin, sessionId);

        var row = (await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray().Single();

        // An observation is one delivery type, not a separate history. BR-07 decided that it
        // deducts; having deducted, it sits here beside everything else.
        Assert.Equal(sessionId, row.GetProperty("id").GetGuid());
        Assert.Equal("Observation", row.GetProperty("deliveryType").GetString());
        Assert.Equal("Attended", row.GetProperty("status").GetString());
        Assert.Equal(1, row.GetProperty("consumedPackagePosition").GetInt32());
    }

    // --- what does not ------------------------------------------------------

    [Fact]
    public async Task A_scheduled_session_is_not_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – scheduled");

        // Booked and not yet resolved. BR-04: it has taken nothing.
        await factory.AddBookedSessionAsync(profileId, DateTime.UtcNow.AddDays(3));

        var page = await SessionsAsync(admin, packageId);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_cancelled_session_is_not_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – cancelled");

        var sessionId = await ObservationAsync(
            admin, profileId, deductSession: true, DateTime.UtcNow.AddDays(3));

        await CancelAsync(admin, sessionId);

        // BR-06: cancelling never deducts, so the package was never spent on it - even though
        // the Admin had said this observation would deduct if it went ahead.
        Assert.Empty((await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_no_show_the_coach_did_not_charge_is_not_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – free no-show");

        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId, "NoShow", deductSession: false);

        // It happened and it is real history, but it cost the athlete nothing, so it is not part
        // of what this package was spent on. GET /sessions is where it appears.
        Assert.Empty((await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
    }

    [Fact]
    public async Task An_observation_the_coach_chose_not_to_deduct_is_not_in_the_list()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – free observation");

        var sessionId = await ObservationAsync(admin, profileId, deductSession: false, Past);
        await AttendAsync(admin, sessionId);

        // BR-07: the Admin said it does not deduct, so it took nothing and is not in the list.
        Assert.Empty((await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
    }

    // --- the whole mix, and the count that makes it trustworthy ---------------

    /// <summary>
    /// The guarantee the endpoint is documented on: the number of rows is the package's
    /// <c>usedSessions</c>, and the positions are exactly 1..usedSessions with none missing and
    /// none repeated. Everything else in this file is one half of this.
    /// </summary>
    [Fact]
    public async Task The_list_holds_every_deduction_and_only_the_deductions()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – the mix");

        // Three that deduct...
        var attended = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, attended);

        var chargedNoShow = await factory.AddBookedSessionAsync(profileId, Past.AddDays(1));
        await AttendAsync(admin, chargedNoShow, "NoShow", deductSession: true);

        var observation = await ObservationAsync(admin, profileId, deductSession: true, Past.AddDays(2));
        await AttendAsync(admin, observation);

        // ...and four that do not.
        var freeNoShow = await factory.AddBookedSessionAsync(profileId, Past.AddDays(3));
        await AttendAsync(admin, freeNoShow, "NoShow", deductSession: false);

        var freeObservation = await ObservationAsync(admin, profileId, deductSession: false, Past.AddDays(4));
        await AttendAsync(admin, freeObservation);

        var cancelled = await ObservationAsync(
            admin, profileId, deductSession: true, DateTime.UtcNow.AddDays(3));
        await CancelAsync(admin, cancelled);

        var scheduled = await factory.AddBookedSessionAsync(profileId, DateTime.UtcNow.AddDays(4));

        var page = await SessionsAsync(admin, packageId);
        var ids = IdsOf(page);

        Assert.Equal([attended, chargedNoShow, observation], ids);
        Assert.DoesNotContain(freeNoShow, ids);
        Assert.DoesNotContain(freeObservation, ids);
        Assert.DoesNotContain(cancelled, ids);
        Assert.DoesNotContain(scheduled, ids);

        // The count matches the balance, which is the property that makes this list an honest
        // answer to "what were my sessions spent on?" rather than an approximation of one.
        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        var used = package.GetProperty("usedSessions").GetInt32();

        Assert.Equal(3, used);
        Assert.Equal(used, page.GetProperty("totalCount").GetInt32());

        // 1..used exactly: no gap, no repeat.
        var positions = page.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("consumedPackagePosition").GetInt32())
            .ToArray();

        Assert.Equal([.. Enumerable.Range(1, used)], [.. positions.Order()]);
    }

    // --- ordering ------------------------------------------------------------

    [Fact]
    public async Task The_list_runs_oldest_first_so_the_package_reads_as_session_one_two_three()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – ordering");

        var third = await factory.AddBookedSessionAsync(profileId, Past.AddDays(20));
        var first = await factory.AddBookedSessionAsync(profileId, Past);
        var second = await factory.AddBookedSessionAsync(profileId, Past.AddDays(10));

        // Resolved out of order on purpose, so the assertion below is about the session's start
        // time and not about the order the coach happened to tap them in.
        await AttendAsync(admin, second);
        await AttendAsync(admin, third);
        await AttendAsync(admin, first);

        var page = await SessionsAsync(admin, packageId);

        Assert.Equal([first, second, third], IdsOf(page));

        var starts = page.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("startUtc").GetDateTime())
            .ToArray();

        Assert.Equal([.. starts.Order()], starts);

        // consumedPackagePosition records the order they were DEDUCTED in, which here is the
        // order the coach resolved them - deliberately not the row order. The contract says so,
        // and this is what says it is true.
        var positions = page.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("consumedPackagePosition").GetInt32())
            .ToArray();

        Assert.Equal([3, 1, 2], positions);
    }

    [Fact]
    public async Task Paging_walks_the_list_oldest_first_without_repeating_or_skipping()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – paged");

        var expected = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var sessionId = await factory.AddBookedSessionAsync(profileId, Past.AddDays(i));
            await AttendAsync(admin, sessionId);
            expected.Add(sessionId);
        }

        var seen = new List<Guid>();
        var page = 1;

        while (true)
        {
            var body = await SessionsAsync(admin, packageId, $"page={page}&pageSize=2");

            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, body.GetProperty("totalPages").GetInt32());

            seen.AddRange(IdsOf(body));

            if (!body.GetProperty("hasNextPage").GetBoolean()) break;
            page++;
        }

        Assert.Equal(3, page);
        Assert.Equal(expected, seen);
    }

    // --- the envelope and the empty case -------------------------------------

    [Fact]
    public async Task A_package_nothing_has_been_spent_from_is_an_empty_page_rather_than_a_404()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – untouched");

        var body = await SessionsAsync(admin, packageId);

        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        // A package bought this morning has been spent on nothing, which is a real answer and
        // the screen's empty view - not a missing resource.
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("page=0", 1, 20)]
    [InlineData("page=-3", 1, 20)]
    [InlineData("pageSize=0", 1, 1)]
    [InlineData("pageSize=9999", 1, 100)]
    public async Task Paging_outside_the_range_is_clamped_rather_than_rejected(
        string query, int expectedPage, int expectedPageSize)
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var packageId = await PurchaseAsync(admin, athleteId, $"Sessions – clamp {query}");

        var body = await SessionsAsync(admin, packageId, query);

        Assert.Equal(expectedPage, body.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, body.GetProperty("pageSize").GetInt32());
    }

    // --- authorisation -------------------------------------------------------

    [Fact]
    public async Task An_unknown_package_is_404_rather_than_an_empty_page()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/packages/{Guid.NewGuid()}/sessions");

        // An empty page is a real answer, so a bad id must not be able to look like one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PACKAGE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// The catalogue id and the purchased-package id are different things, and confusing them is
    /// the likeliest client mistake. A packageOptionId must not resolve here — it would otherwise
    /// be a way to ask about every athlete who ever bought that option.
    /// </summary>
    [Fact]
    public async Task A_package_option_id_is_404_rather_than_the_sessions_of_everyone_who_bought_it()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – option id");

        var optionId = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}"))
            .GetProperty("packageOptionId").GetGuid();

        Assert.NotEqual(packageId, optionId);

        var response = await admin.GetAsync($"/api/v1/packages/{optionId}/sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_package_belonging_to_another_coach_is_404()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – foreign");

        // The coach id is taken from the token, never the route, so moving the package to another
        // coach is enough to make it unreachable - no second Admin account is needed to prove it.
        await factory.QueryAsync(async db =>
            await db.Database.ExecuteSqlAsync(
                $"""update "PurchasedPackages" set "CoachId" = {Guid.NewGuid()} where "Id" = {packageId}"""));

        var response = await admin.GetAsync($"/api/v1/packages/{packageId}/sessions");

        // The same 404 as an id that does not exist, so a package cannot be probed for existence.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PACKAGE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_use_the_admin_route_and_an_anonymous_caller_cannot_either()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – policy");

        var athlete = await AthleteClientAsync(email);

        // Their own package, and still forbidden: the Admin route is Admin-only whatever the id.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync($"/api/v1/packages/{packageId}/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync($"/api/v1/packages/{packageId}/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync($"/api/v1/me/packages/{packageId}/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync($"/api/v1/me/packages/{packageId}/sessions")).StatusCode);
    }

    // --- the athlete's own view ----------------------------------------------

    /// <summary>
    /// The whole point of the athlete route: the mobile app reuses one screen for both roles, so
    /// the two responses have to be the same thing, not merely similar.
    /// </summary>
    [Fact]
    public async Task The_athlete_sees_exactly_what_the_admin_sees_for_their_package()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – shared");

        var attended = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, attended);

        var observation = await ObservationAsync(admin, profileId, deductSession: true, Past.AddDays(1));
        await AttendAsync(admin, observation);

        var athlete = await AthleteClientAsync(email);

        var theirs = await MySessionsAsync(athlete, packageId);
        var coachs = await SessionsAsync(admin, packageId);

        Assert.Equal(coachs.GetRawText(), theirs.GetRawText());
        Assert.Equal([attended, observation], IdsOf(theirs));
    }

    /// <summary>
    /// There is no athlete id on the athlete route, so the only thing that could leak another
    /// athlete's sessions is a borrowed package id. This is what says it does not.
    /// </summary>
    [Fact]
    public async Task An_athlete_cannot_read_another_athletes_package()
    {
        var admin = await AdminClientAsync();

        var (mineId, myEmail) = await factory.NewAthleteAsync();
        var (theirsId, _) = await factory.NewAthleteAsync();

        await PurchaseAsync(admin, mineId, "Sessions – mine");
        var theirPackageId = await PurchaseAsync(admin, theirsId, "Sessions – theirs");

        var theirProfileId = await ProfileIdAsync(theirsId);
        var theirSession = await factory.AddBookedSessionAsync(theirProfileId, Past);
        await AttendAsync(admin, theirSession);

        var me = await AthleteClientAsync(myEmail);

        var response = await me.GetAsync($"/api/v1/me/packages/{theirPackageId}/sessions");

        // 404, not 403: the same answer as an id that does not exist, so one athlete cannot even
        // learn that another's package id is real.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PACKAGE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_athlete_can_read_a_package_they_have_finished_with()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – closed");

        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId);

        (await admin.PostAsync($"/api/v1/packages/{packageId}/close", null)).EnsureSuccessStatusCode();

        var athlete = await AthleteClientAsync(email);

        // History is the point of this endpoint: a closed package is exactly the case the
        // Package History screen opens it for, and it still reads.
        Assert.Equal([sessionId], IdsOf(await MySessionsAsync(athlete, packageId)));
    }

    /// <summary>
    /// Historical accuracy: a past package's sessions must still read after the catalogue entry
    /// it came from is archived. The package carries its own snapshot, and nothing in this list
    /// is looked up from the option.
    /// </summary>
    [Fact]
    public async Task Archiving_the_catalogue_option_does_not_disturb_a_packages_session_history()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – archived option");

        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");
        var optionId = package.GetProperty("packageOptionId").GetGuid();

        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");

        var archived = await admin.PostAsJsonAsync($"/api/v1/package-options/{optionId}/archive",
            new { version = option.GetProperty("version").GetInt32() });

        archived.EnsureSuccessStatusCode();

        Assert.Equal([sessionId], IdsOf(await SessionsAsync(admin, packageId)));
    }

    // --- the expandable row --------------------------------------------------

    [Fact]
    public async Task A_row_says_whether_the_session_has_notes_to_expand_into()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – notes");

        var withNote = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, withNote);

        var withoutNote = await factory.AddBookedSessionAsync(profileId, Past.AddDays(1));
        await AttendAsync(admin, withoutNote);

        var note = await admin.PostAsJsonAsync($"/api/v1/sessions/{withNote}/notes",
            new { title = "Breathing drill", content = "Held the rhythm through the third set." });

        note.EnsureSuccessStatusCode();

        var rows = (await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray().ToArray();

        Assert.True(rows.Single(x => x.GetProperty("id").GetGuid() == withNote)
            .GetProperty("hasNotes").GetBoolean());

        Assert.False(rows.Single(x => x.GetProperty("id").GetGuid() == withoutNote)
            .GetProperty("hasNotes").GetBoolean());
    }

    /// <summary>
    /// The row carries what the screen needs and nothing from the Admin's side of the record.
    /// Stated as an exact field list because a field added here reaches the athlete too.
    /// </summary>
    [Fact]
    public async Task A_row_carries_the_fields_the_contract_promises_and_no_others()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var profileId = await ProfileIdAsync(athleteId);
        var packageId = await PurchaseAsync(admin, athleteId, "Sessions – shape");

        var sessionId = await factory.AddBookedSessionAsync(profileId, Past);
        await AttendAsync(admin, sessionId);

        var row = (await SessionsAsync(admin, packageId)).GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            ["id", "startUtc", "endUtc", "durationMinutes", "deliveryType", "status",
             "locationOrPlatform", "consumedPackagePosition", "attendedAtUtc", "hasNotes"],
            row.EnumerateObject().Select(p => p.Name).ToArray());

        // Neither the coach's booking links nor the Calendly identifiers belong in a history
        // view, and attendedByUserId is the Admin's side of the record.
        foreach (var absent in new[]
                 {
                     "meetingUrl", "rescheduleUrl", "cancelUrl", "attendedByUserId",
                     "calendlyEventUri", "athleteName", "athleteProfileId", "packageId"
                 })
        {
            Assert.False(row.TryGetProperty(absent, out _), $"{absent} must not be exposed");
        }
    }
}
