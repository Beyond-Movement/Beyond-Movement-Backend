using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 9: the Admin Home aggregate.
/// <para>
/// The fixture pins "now" to a Thursday and seeds sessions on both sides of it, so each period
/// includes a different, known subset and the expected numbers are written down rather than
/// recomputed by the test — a test that calculates its own expectation with the same code as the
/// endpoint proves only that the code agrees with itself.
/// </para>
/// </summary>
public sealed class DashboardTests(DashboardApiFactory factory) : IClassFixture<DashboardApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private Task<HttpClient> AdminClientAsync() =>
        SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private Task<HttpClient> AthleteClientAsync() =>
        SignInAsync(factory.AthleteEmail, AthleteApiFactory.AthletePassword);

    private async Task<JsonElement> DashboardAsync(string query = "")
    {
        var admin = await AdminClientAsync();
        var response = await admin.GetAsync($"/api/v1/dashboard/admin{query}");

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static (int Sessions, int Minutes, int Online, int F2F, int Observation) Read(JsonElement stats) => (
        stats.GetProperty("attendedSessions").GetInt32(),
        stats.GetProperty("totalMinutes").GetInt32(),
        stats.GetProperty("onlineSessions").GetInt32(),
        stats.GetProperty("faceToFaceSessions").GetInt32(),
        stats.GetProperty("observationSessions").GetInt32());

    // --- the four period filters --------------------------------------------

    [Theory]
    // Weekly: the two sessions inside the Cairo week of the pinned Thursday.       60 + 90
    [InlineData("Weekly", 2, 150, 1, 1, 0)]
    // Monthly: adds the observation earlier in March.                             + 45
    [InlineData("Monthly", 3, 195, 1, 1, 1)]
    // Yearly: adds the January session.                                           + 30
    [InlineData("Yearly", 4, 225, 2, 1, 1)]
    // All time: adds last June's.                                                 + 120
    [InlineData("AllTime", 5, 345, 3, 1, 1)]
    public async Task Each_period_counts_only_the_sessions_inside_it(
        string period, int sessions, int minutes, int online, int faceToFace, int observation)
    {
        var stats = (await DashboardAsync($"?period={period}")).GetProperty("statistics");

        Assert.Equal(period, stats.GetProperty("period").GetString());
        Assert.Equal((sessions, minutes, online, faceToFace, observation), Read(stats));
    }

    [Fact]
    public async Task The_period_defaults_to_monthly()
    {
        var stats = (await DashboardAsync()).GetProperty("statistics");

        Assert.Equal("Monthly", stats.GetProperty("period").GetString());
        Assert.Equal(3, stats.GetProperty("attendedSessions").GetInt32());
    }

    [Fact]
    public async Task All_time_has_no_window_and_the_others_do()
    {
        var allTime = (await DashboardAsync("?period=AllTime")).GetProperty("statistics");
        Assert.Equal(JsonValueKind.Null, allTime.GetProperty("fromUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, allTime.GetProperty("toUtc").ValueKind);

        var monthly = (await DashboardAsync("?period=Monthly")).GetProperty("statistics");
        var from = monthly.GetProperty("fromUtc").GetDateTime();
        var to = monthly.GetProperty("toUtc").GetDateTime();

        // Cairo is UTC+2 in March, so the month begins at 22:00 UTC on 28 February - NOT at
        // midnight UTC on the 1st. This is the whole reason the coach's zone is honoured.
        Assert.Equal(new DateTime(2026, 2, 28, 22, 0, 0), from);
        Assert.Equal(new DateTime(2026, 3, 31, 22, 0, 0), to);
        Assert.Equal("Africa/Cairo", monthly.GetProperty("timeZone").GetString());
    }

    // --- delivered vs everything else ---------------------------------------

    [Fact]
    public async Task Cancelled_no_show_and_unresolved_sessions_never_count()
    {
        // The fixture puts one of each inside the current week, alongside the two attended ones.
        // If any leaked in, the weekly figures below would move.
        var stats = (await DashboardAsync("?period=Weekly")).GetProperty("statistics");

        Assert.Equal(2, stats.GetProperty("attendedSessions").GetInt32());
        Assert.Equal(150, stats.GetProperty("totalMinutes").GetInt32());

        // Each of the three excluded sessions is 60 minutes, so any one of them showing up
        // would be visible here.
        Assert.NotEqual(210, stats.GetProperty("totalMinutes").GetInt32());
    }

    [Fact]
    public async Task Another_coachs_sessions_are_invisible()
    {
        // The foreign coach's session is 600 minutes on a day inside every window.
        foreach (var period in new[] { "Weekly", "Monthly", "Yearly", "AllTime" })
        {
            var stats = (await DashboardAsync($"?period={period}")).GetProperty("statistics");
            Assert.True(stats.GetProperty("totalMinutes").GetInt32() < 600,
                $"{period} appears to include another coach's session");
        }
    }

    // --- the breakdown ------------------------------------------------------

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Yearly")]
    [InlineData("AllTime")]
    public async Task The_delivery_breakdown_always_sums_to_the_total(string period)
    {
        var stats = (await DashboardAsync($"?period={period}")).GetProperty("statistics");
        var (sessions, _, online, faceToFace, observation) = Read(stats);

        Assert.Equal(sessions, online + faceToFace + observation);
    }

    // --- coaching hours -----------------------------------------------------

    [Fact]
    public async Task Coaching_time_is_the_sum_of_the_stored_durations()
    {
        // 60 + 90 + 45 = 195 minutes across March, from each session's own DurationMinutes.
        var stats = (await DashboardAsync("?period=Monthly")).GetProperty("statistics");

        Assert.Equal(195, stats.GetProperty("totalMinutes").GetInt32());

        // An integer count of minutes, never a decimal number of hours - the client divides.
        Assert.Equal(JsonValueKind.Number, stats.GetProperty("totalMinutes").ValueKind);
        Assert.False(stats.TryGetProperty("totalHours", out _));
    }

    [Fact]
    public async Task A_delivery_type_with_nothing_in_the_period_reports_zero()
    {
        // No observation was delivered inside the current week, though one was earlier in the
        // month - so this zero is a real empty group rather than a missing row in the response.
        var weekly = (await DashboardAsync("?period=Weekly")).GetProperty("statistics");
        Assert.Equal(0, weekly.GetProperty("observationSessions").GetInt32());

        var monthly = (await DashboardAsync("?period=Monthly")).GetProperty("statistics");
        Assert.Equal(1, monthly.GetProperty("observationSessions").GetInt32());
    }

    // --- upcoming sessions --------------------------------------------------

    [Fact]
    public async Task Upcoming_returns_the_next_three_scheduled_sessions_in_order()
    {
        var upcoming = (await DashboardAsync()).GetProperty("upcomingSessions");
        var ids = upcoming.EnumerateArray().Select(x => x.GetProperty("sessionId").GetGuid()).ToArray();

        Assert.Equal(3, ids.Length);
        Assert.Equal(factory.ExpectedUpcoming, ids);

        // Ordered by start, soonest first.
        var starts = upcoming.EnumerateArray()
            .Select(x => x.GetProperty("scheduledStartUtc").GetDateTime()).ToArray();
        Assert.Equal(starts.OrderBy(x => x), starts);
    }

    [Fact]
    public async Task Upcoming_excludes_cancelled_and_past_sessions()
    {
        var upcoming = (await DashboardAsync("?upcomingLimit=20")).GetProperty("upcomingSessions");

        Assert.All(upcoming.EnumerateArray(), card =>
        {
            Assert.True(card.GetProperty("scheduledStartUtc").GetDateTime() >= DashboardApiFactory.Now);

            // The cancelled future session sits 6 hours out - sooner than all four scheduled
            // ones - so it would head this list if cancellation were not excluded.
            Assert.NotEqual(DashboardApiFactory.Now.AddHours(6),
                card.GetProperty("scheduledStartUtc").GetDateTime());
        });

        // Four scheduled ahead, and the cancelled one is not among them.
        Assert.Equal(4, upcoming.GetArrayLength());
    }

    [Fact]
    public async Task Upcoming_does_not_change_when_the_statistics_period_changes()
    {
        string[] serialised = [];

        foreach (var period in new[] { "Weekly", "Monthly", "Yearly", "AllTime" })
        {
            var body = await DashboardAsync($"?period={period}");
            var stats = body.GetProperty("statistics");
            var upcoming = body.GetProperty("upcomingSessions").GetRawText();

            // The statistics really are changing between these calls...
            Assert.True(stats.GetProperty("attendedSessions").GetInt32() > 0);

            // ...while the upcoming list is byte-for-byte identical every time.
            if (serialised.Length == 0) serialised = [upcoming];
            else Assert.Equal(serialised[0], upcoming);
        }
    }

    [Fact]
    public async Task Each_upcoming_card_carries_the_athletes_user_id_and_name()
    {
        var card = (await DashboardAsync()).GetProperty("upcomingSessions").EnumerateArray().First();

        // The USER id, which is what GET /athletes/{athleteId} takes - not the profile id.
        Assert.Equal(factory.AthleteUserId, card.GetProperty("athleteUserId").GetGuid());
        Assert.Equal("Dash Athlete", card.GetProperty("athleteName").GetString());

        // The profile id is deliberately absent: the card exists to navigate to Athlete Profile.
        Assert.False(card.TryGetProperty("athleteProfileId", out _));

        Assert.Equal(60, card.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Online", card.GetProperty("deliveryType").GetString());
    }

    [Fact]
    public async Task The_upcoming_limit_is_clamped_rather_than_rejected()
    {
        Assert.Equal(4, (await DashboardAsync("?upcomingLimit=999"))
            .GetProperty("upcomingSessions").GetArrayLength());

        // Zero and negatives fall back to the default rather than returning an empty list.
        Assert.Equal(3, (await DashboardAsync("?upcomingLimit=0"))
            .GetProperty("upcomingSessions").GetArrayLength());
        Assert.Equal(3, (await DashboardAsync("?upcomingLimit=-5"))
            .GetProperty("upcomingSessions").GetArrayLength());
    }

    // --- authorization ------------------------------------------------------

    [Fact]
    public async Task The_dashboard_is_admin_only()
    {
        var athlete = await AthleteClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync("/api/v1/dashboard/admin")).StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/dashboard/admin")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_period_is_rejected_rather_than_silently_defaulted()
    {
        var admin = await AdminClientAsync();
        var response = await admin.GetAsync("/api/v1/dashboard/admin?period=Fortnightly");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
