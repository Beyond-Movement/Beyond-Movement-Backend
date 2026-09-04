using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// A factory of its own: these tests move the Admin's time zone, which is the value the whole
/// dashboard suite computes its period boundaries from.
/// </summary>
public sealed class TimeZoneSyncApiFactory : ApiFactory;

/// <summary>
/// Device time-zone synchronisation — the flow that replaces a manual setting the app will
/// never show: detect the device zone, compare with <c>/auth/me</c>, write only on a difference.
/// </summary>
public sealed class TimeZoneSyncTests(TimeZoneSyncApiFactory factory) : IClassFixture<TimeZoneSyncApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

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

    private static async Task<string> ReadZoneAsync(HttpClient client)
    {
        var me = await client.GetAsync("/api/v1/auth/me");
        me.EnsureSuccessStatusCode();

        return (await me.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("timeZone").GetString()!;
    }

    [Fact]
    public async Task Auth_me_carries_the_stored_zone_so_the_app_has_something_to_compare_against()
    {
        var client = await AdminClientAsync();

        // Seed:AdminTimeZone is Africa/Cairo in appsettings.json, which the test host loads.
        Assert.Equal("Africa/Cairo", await ReadZoneAsync(client));
    }

    [Fact]
    public async Task Writing_a_new_zone_returns_it_and_auth_me_agrees_immediately()
    {
        var client = await AdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/timezone",
            new { timeZone = "Europe/London" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var written = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("timeZone").GetString();

        Assert.Equal("Europe/London", written);
        Assert.Equal("Europe/London", await ReadZoneAsync(client));

        await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = "Africa/Cairo" });
    }

    /// <summary>
    /// The property the sync flow rests on: what the device sends is what <c>/auth/me</c> gives
    /// back, so the app's comparison matches on the next launch and it stops writing. Storing
    /// <c>TimeZoneInfo.Id</c> instead would turn "Africa/Cairo" into "Egypt Standard Time" on a
    /// Windows host, and the app would re-sync forever.
    /// </summary>
    [Fact]
    public async Task The_zone_round_trips_unchanged_so_the_app_settles_after_one_write()
    {
        var client = await AdminClientAsync();

        await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = "Asia/Tokyo" });

        var readBack = await ReadZoneAsync(client);
        Assert.Equal("Asia/Tokyo", readBack);

        // Exactly what the app would do next launch: same zone, so it must not call again -
        // and if it does, nothing changes.
        var repeat = await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = readBack });

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("Asia/Tokyo", await ReadZoneAsync(client));

        await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = "Africa/Cairo" });
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("GMT+3")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unresolvable_zone_is_refused_and_leaves_the_stored_one_alone(string bad)
    {
        var client = await AdminClientAsync();
        var before = await ReadZoneAsync(client);

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = bad });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TIME_ZONE_INVALID", problem.GetProperty("errorCode").GetString());

        // Refused rather than ignored: the dashboard's resolver falls back to UTC without
        // complaint, so a bad value that got through would show up only as wrong figures.
        Assert.Equal(before, await ReadZoneAsync(client));
    }

    [Fact]
    public async Task A_zone_too_long_for_the_column_is_refused_rather_than_failing_in_the_database()
    {
        var client = await AdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/auth/me/timezone",
            new { timeZone = new string('x', 200) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TIME_ZONE_INVALID", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Syncing_a_zone_requires_a_token()
    {
        var anonymous = factory.CreateClient();

        var response = await anonymous.PutAsJsonAsync("/api/v1/auth/me/timezone",
            new { timeZone = "Africa/Cairo" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The reason the whole feature exists: the dashboard's period boundaries follow the zone
    /// the app reports, so a coach who travels gets their own calendar without being asked.
    /// </summary>
    [Fact]
    public async Task The_dashboard_reports_the_zone_the_app_last_synced()
    {
        var client = await AdminClientAsync();

        try
        {
            await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = "Asia/Tokyo" });

            var dashboard = await client.GetAsync("/api/v1/dashboard/admin?period=Weekly");
            dashboard.EnsureSuccessStatusCode();

            var statistics = (await dashboard.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("statistics");

            Assert.Equal("Asia/Tokyo", statistics.GetProperty("timeZone").GetString());
        }
        finally
        {
            await client.PutAsJsonAsync("/api/v1/auth/me/timezone", new { timeZone = "Africa/Cairo" });
        }
    }
}
