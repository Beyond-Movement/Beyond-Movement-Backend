using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture, because paging assertions count rows and the shared purchase fixture has
/// other tests adding them concurrently.
/// </summary>
public sealed class PurchaseListingApiFactory : PurchaseApiFactory;

/// <summary>
/// What the Admin payments screen reads: the athlete label carried on every purchase, and the
/// paging that replaced an unbounded list.
/// <para>
/// The label exists so the screen does not have to download the athlete directory to put a name
/// on a row. The pair matters more than either field: an athlete who never finished Complete
/// Profile has no name, and the email behind it is what the athlete list itself falls back to.
/// </para>
/// </summary>
public sealed class PurchaseListingTests(PurchaseListingApiFactory factory)
    : IClassFixture<PurchaseListingApiFactory>
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

    private Task<HttpClient> AthleteClientAsync(string email) =>
        SignInAsync(email, AthleteApiFactory.AthletePassword);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Guid> CreateOptionAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name,
            sessions = 8,
            defaultPriceMinor = 400_000L,
            features = new[] { "Weekly video call" }
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SelectAsync(string athleteEmail, Guid optionId)
    {
        var athlete = await AthleteClientAsync(athleteEmail);
        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    // --- the athlete label ---------------------------------------------------

    /// <summary>
    /// The three shapes that carry a purchase must agree, or the screen shows a name in a list
    /// and loses it the moment the coach confirms the payment.
    /// </summary>
    [Fact]
    public async Task Every_purchase_response_carries_the_athlete_name_and_email()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Labelled 8");
        var (userId, email) = await factory.NewAthleteAsync();

        var purchaseId = await SelectAsync(email, optionId);

        void AssertLabelled(JsonElement purchase)
        {
            Assert.Equal(userId, purchase.GetProperty("athleteUserId").GetGuid());
            Assert.False(string.IsNullOrWhiteSpace(
                purchase.GetProperty("athleteName").GetString()));
            Assert.Equal(email, purchase.GetProperty("athleteEmail").GetString());
        }

        var listed = (await BodyAsync(
                await admin.GetAsync($"/api/v1/purchases?athleteId={userId}")))
            .GetProperty("items").EnumerateArray().Single();
        AssertLabelled(listed);

        AssertLabelled(await BodyAsync(await admin.GetAsync($"/api/v1/purchases/{purchaseId}")));

        var confirmed = await BodyAsync(
            await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null));
        AssertLabelled(confirmed.GetProperty("purchase"));

        // Confirming twice is idempotent, and the label must survive that path too - it is a
        // different branch in the service, and the one a retry after a timeout takes.
        var repeat = await BodyAsync(
            await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null));
        Assert.True(repeat.GetProperty("alreadyPaid").GetBoolean());
        AssertLabelled(repeat.GetProperty("purchase"));
    }

    /// <summary>
    /// The case the pair exists for. An athlete who registered and never finished Complete
    /// Profile has no name, and the app falls back to the email exactly as the athlete list does.
    /// </summary>
    [Fact]
    public async Task An_athlete_with_no_name_yet_still_has_an_email_to_show()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Nameless 8");
        var (userId, email) = await factory.NewNamelessAthleteAsync();

        await SelectAsync(email, optionId);

        var purchase = (await BodyAsync(
                await admin.GetAsync($"/api/v1/purchases?athleteId={userId}")))
            .GetProperty("items").EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("athleteName").ValueKind);
        Assert.Equal(email, purchase.GetProperty("athleteEmail").GetString());
    }

    /// <summary>
    /// The athlete's own purchase carries the label too. It is the same shape, so leaving it
    /// unpopulated on this route would make the field mean "sometimes present".
    /// </summary>
    [Fact]
    public async Task The_athletes_own_current_purchase_is_labelled_as_well()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Own 8");
        var (_, email) = await factory.NewAthleteAsync();

        await SelectAsync(email, optionId);

        var athlete = await AthleteClientAsync(email);
        var current = await BodyAsync(await athlete.GetAsync("/api/v1/me/purchases/current"));

        Assert.False(string.IsNullOrWhiteSpace(current.GetProperty("athleteName").GetString()));
        Assert.Equal(email, current.GetProperty("athleteEmail").GetString());
    }

    /// <summary>
    /// The name is read at request time, not frozen with the price. The snapshot records what
    /// was bought; who bought it is not part of that bargain, and a renamed athlete should not
    /// see an old name on their own history.
    /// </summary>
    [Fact]
    public async Task The_label_follows_a_renamed_athlete_rather_than_the_snapshot()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Renamed 8");
        var (userId, email) = await factory.NewAthleteAsync();

        await SelectAsync(email, optionId);

        await factory.QueryAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.SetFullName("Renamed Athlete", DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        var purchase = (await BodyAsync(
                await admin.GetAsync($"/api/v1/purchases?athleteId={userId}")))
            .GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Renamed Athlete", purchase.GetProperty("athleteName").GetString());
    }

    // --- paging --------------------------------------------------------------

    [Fact]
    public async Task The_list_comes_back_in_the_same_envelope_as_the_athlete_list()
    {
        var admin = await AdminClientAsync();

        var body = await BodyAsync(await admin.GetAsync("/api/v1/purchases"));

        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Paging_walks_the_whole_list_without_repeating_or_skipping_a_row()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Paged 8");

        // Five purchases for one athlete: each is paid before the next is selected, because an
        // athlete may hold only one pending purchase and one active package at a time.
        var (userId, email) = await factory.NewAthleteAsync();
        var expected = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var purchaseId = await SelectAsync(email, optionId);
            expected.Add(purchaseId);

            var paid = await BodyAsync(
                await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null));

            await admin.PostAsJsonAsync(
                $"/api/v1/packages/{paid.GetProperty("package").GetProperty("id").GetGuid()}/close",
                new { });
        }

        var seen = new List<Guid>();
        var page = 1;

        while (true)
        {
            var body = await BodyAsync(await admin.GetAsync(
                $"/api/v1/purchases?athleteId={userId}&page={page}&pageSize=2"));

            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, body.GetProperty("totalPages").GetInt32());

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetGuid()));

            if (!body.GetProperty("hasNextPage").GetBoolean()) break;
            page++;
        }

        Assert.Equal(3, page);

        // Every purchase exactly once. Ordering ties are broken by id, so a row cannot land on
        // two pages or fall between them.
        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal([.. expected.OrderBy(x => x)], [.. seen.OrderBy(x => x)]);
    }

    [Fact]
    public async Task Total_count_describes_the_filter_rather_than_everything()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Counted 8");

        var (pendingUserId, pendingEmail) = await factory.NewAthleteAsync();
        await SelectAsync(pendingEmail, optionId);

        var forOne = await BodyAsync(
            await admin.GetAsync($"/api/v1/purchases?athleteId={pendingUserId}"));
        var everything = await BodyAsync(await admin.GetAsync("/api/v1/purchases"));

        Assert.Equal(1, forOne.GetProperty("totalCount").GetInt32());
        Assert.True(everything.GetProperty("totalCount").GetInt32() >= 1);

        // Filters apply before paging, so a filtered count can never exceed the unfiltered one.
        Assert.True(
            forOne.GetProperty("totalCount").GetInt32()
            <= everything.GetProperty("totalCount").GetInt32());

        var pendingOnly = await BodyAsync(
            await admin.GetAsync($"/api/v1/purchases?athleteId={pendingUserId}&status=Pending"));
        Assert.Equal(1, pendingOnly.GetProperty("totalCount").GetInt32());

        var paidOnly = await BodyAsync(
            await admin.GetAsync($"/api/v1/purchases?athleteId={pendingUserId}&status=Paid"));
        Assert.Equal(0, paidOnly.GetProperty("totalCount").GetInt32());
        Assert.Empty(paidOnly.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("page=0", 1, 20)]
    [InlineData("page=-5", 1, 20)]
    [InlineData("pageSize=0", 1, 1)]
    [InlineData("pageSize=5000", 1, 100)]
    [InlineData("pageSize=-1", 1, 1)]
    public async Task Paging_outside_the_range_is_clamped_rather_than_rejected(
        string query, int expectedPage, int expectedPageSize)
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/purchases?{query}");

        // Clamped, matching GET /athletes: a careless page size is a client bug the server
        // absorbs, not a 400 the coach has to see.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.Equal(expectedPage, body.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task A_page_past_the_end_is_an_empty_page_not_an_error()
    {
        var admin = await AdminClientAsync();

        var body = await BodyAsync(await admin.GetAsync("/api/v1/purchases?page=9999&pageSize=10"));

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.False(body.GetProperty("hasNextPage").GetBoolean());
        Assert.True(body.GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_athlete_is_still_404_rather_than_an_empty_page()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/purchases?athleteId={Guid.NewGuid()}&page=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND",
            (await BodyAsync(response)).GetProperty("errorCode").GetString());
    }
}
