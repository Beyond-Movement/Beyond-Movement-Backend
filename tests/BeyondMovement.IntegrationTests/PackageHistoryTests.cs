using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests count an athlete's packages, so nothing else may be adding them.
/// </summary>
public sealed class PackageHistoryApiFactory : PurchaseApiFactory;

/// <summary>
/// The Athlete Profile's Package History — every package the athlete has had, and the paging
/// that replaced an unbounded list.
/// <para>
/// The rule the profile depends on is that the newest non-Active entry is the "most recent
/// previous package". That only holds because at most one package is Active (BR-03) and it is
/// always the newest, so it sorts first when it exists — which is what these pin.
/// </para>
/// </summary>
public sealed class PackageHistoryTests(PackageHistoryApiFactory factory)
    : IClassFixture<PackageHistoryApiFactory>
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

    private static async Task<Guid> CreateOptionAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name, sessions = 8, defaultPriceMinor = 400_000L, features = new[] { "Weekly video call" }
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> HistoryAsync(
        HttpClient admin, Guid athleteId, string query = "")
    {
        var response = await admin.GetAsync(
            $"/api/v1/athletes/{athleteId}/packages{(query.Length == 0 ? "" : "?" + query)}");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Sells one package and closes it, so the next can be sold under BR-03.</summary>
    private static async Task<Guid> SellAndCloseAsync(HttpClient admin, Guid athleteId, Guid optionId)
    {
        var sold = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/packages", new { packageOptionId = optionId });

        sold.EnsureSuccessStatusCode();
        var packageId = (await sold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await admin.PostAsync($"/api/v1/packages/{packageId}/close", null)).EnsureSuccessStatusCode();

        return packageId;
    }

    // --- the envelope --------------------------------------------------------

    [Fact]
    public async Task History_comes_back_in_the_same_envelope_as_athletes_and_purchases()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();

        var body = await HistoryAsync(admin, athleteId);

        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        // An athlete who has never bought one gets an empty page, not a 404.
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Paging_walks_the_history_without_repeating_or_skipping_a_package()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "History Paged");
        var (athleteId, _) = await factory.NewAthleteAsync();

        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++)
            expected.Add(await SellAndCloseAsync(admin, athleteId, optionId));

        var seen = new List<Guid>();
        var page = 1;

        while (true)
        {
            var body = await HistoryAsync(admin, athleteId, $"page={page}&pageSize=2");

            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, body.GetProperty("totalPages").GetInt32());

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetGuid()));

            if (!body.GetProperty("hasNextPage").GetBoolean()) break;
            page++;
        }

        Assert.Equal(3, page);
        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal([.. expected.OrderBy(x => x)], [.. seen.OrderBy(x => x)]);
    }

    // --- ordering, and what the profile reads --------------------------------

    /// <summary>
    /// The card rule: the active package sorts first, and the first item that is not Active is
    /// the most recent previous one. The whole Overview tab depends on this holding.
    /// </summary>
    [Fact]
    public async Task The_active_package_sorts_first_and_the_next_item_is_the_previous_one()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "History Current");
        var (athleteId, _) = await factory.NewAthleteAsync();

        var older = await SellAndCloseAsync(admin, athleteId, optionId);
        var previous = await SellAndCloseAsync(admin, athleteId, optionId);

        // Left open, so it is the athlete's active package.
        var current = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/packages", new { packageOptionId = optionId });
        current.EnsureSuccessStatusCode();
        var currentId = (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var items = (await HistoryAsync(admin, athleteId)).GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(currentId, items[0].GetProperty("id").GetGuid());
        Assert.Equal("Active", items[0].GetProperty("status").GetString());

        var mostRecentPrevious = items.First(x => x.GetProperty("status").GetString() != "Active");
        Assert.Equal(previous, mostRecentPrevious.GetProperty("id").GetGuid());

        Assert.Equal(older, items[2].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Active_completed_and_closed_appear_together()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "History Statuses");
        var (athleteId, _) = await factory.NewAthleteAsync();

        await SellAndCloseAsync(admin, athleteId, optionId);

        var open = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/packages", new { packageOptionId = optionId });
        open.EnsureSuccessStatusCode();

        var statuses = (await HistoryAsync(admin, athleteId)).GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("status").GetString())
            .ToArray();

        Assert.Contains("Active", statuses);
        Assert.Contains("Closed", statuses);
    }

    [Fact]
    public async Task A_closed_package_keeps_the_balance_it_had()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "History Balance");
        var (athleteId, _) = await factory.NewAthleteAsync();

        var packageId = await SellAndCloseAsync(admin, athleteId, optionId);

        var closed = (await HistoryAsync(admin, athleteId)).GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == packageId);

        // Closing ends a package without deleting anything, so history still shows what was used.
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(8, closed.GetProperty("totalSessions").GetInt32());
        Assert.Equal(0, closed.GetProperty("usedSessions").GetInt32());
        Assert.Equal(8, closed.GetProperty("remainingSessions").GetInt32());
        Assert.Equal(400_000, closed.GetProperty("pricePaidMinor").GetInt64());
    }

    // --- paging edges --------------------------------------------------------

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

        var body = await HistoryAsync(admin, athleteId, query);

        Assert.Equal(expectedPage, body.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task An_unknown_athlete_is_404_rather_than_an_empty_page()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/athletes/{Guid.NewGuid()}/packages");

        // An empty page is a real answer, so a bad id must not be able to look like one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }
}
