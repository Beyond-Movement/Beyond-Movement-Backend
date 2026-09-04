using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 8: package purchase and manual payment tracking.
/// <para>
/// The five things worth proving here are authorization, that the price is snapshotted rather
/// than trusted or re-read, that the only allowed transition is the only one that happens, that
/// BR-03 still holds at the moment of payment, and that confirming twice produces one package.
/// The last two are the ones that would cost real money to get wrong.
/// </para>
/// </summary>
public sealed class PurchaseTests(PurchaseApiFactory factory) : IClassFixture<PurchaseApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private async Task<HttpClient> AdminClientAsync() =>
        await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteClientAsync(string email) =>
        await SignInAsync(email, AthleteApiFactory.AthletePassword);

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<JsonElement> CreateOptionAsync(
        HttpClient admin, string name, long priceMinor = 400_000, int sessions = 8,
        string[]? features = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name,
            sessions,
            defaultPriceMinor = priceMinor,
            features = features ?? ["Weekly video call", "Session notes"]
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SelectAsync(
        HttpClient athlete, Guid optionId, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        if (response.StatusCode != expected)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ErrorCode(JsonElement problem) => problem.GetProperty("errorCode").GetString()!;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>The rows out of the paged list envelope, which GET /purchases now returns.</summary>
    private static async Task<JsonElement> ItemsAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).GetProperty("items");

    // --- price snapshotting -------------------------------------------------

    [Fact]
    public async Task Selecting_an_option_snapshots_name_sessions_features_and_price()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(
            admin, "Snapshot 8", priceMinor: 400_000, sessions: 8,
            features: ["Weekly video call", "Session notes", "Whiteboard access"]);

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var purchase = await SelectAsync(athlete, option.GetProperty("id").GetGuid());

        Assert.Equal("Snapshot 8", purchase.GetProperty("packageName").GetString());
        Assert.Equal(8, purchase.GetProperty("sessionCount").GetInt32());
        Assert.Equal(400_000, purchase.GetProperty("priceMinor").GetInt64());
        Assert.Equal("EGP", purchase.GetProperty("currency").GetString());
        Assert.Equal("Pending", purchase.GetProperty("status").GetString());
        Assert.Equal("Athlete", purchase.GetProperty("origin").GetString());

        // Order is meaning - it is what the athlete read down the card.
        Assert.Equal(
            ["Weekly video call", "Session notes", "Whiteboard access"],
            purchase.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());

        // Pending means no package yet, and no evidence of payment.
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("purchasedPackageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("paidAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("paidByUserId").ValueKind);
    }

    [Fact]
    public async Task The_snapshotted_price_is_the_loyalty_price_not_the_default()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Loyalty 8", priceMinor: 99_999);

        var (_, email) = await factory.NewAthleteAsync(isLoyal: true);
        var athlete = await AthleteClientAsync(email);

        var purchase = await SelectAsync(athlete, option.GetProperty("id").GetGuid());

        // 999.99 x 0.85 = 849.9915, rounded to the nearest tenth of a pound, away from zero.
        // The app never computes this; the server sends the number it already showed them.
        Assert.Equal(85_000, purchase.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public async Task A_custom_price_overrides_loyalty_and_is_not_discounted_again()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Override 8", priceMinor: 400_000);
        var optionId = option.GetProperty("id").GetGuid();

        var (userId, email) = await factory.NewAthleteAsync(isLoyal: true);

        var set = await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{userId}/custom-prices/{optionId}", new { priceMinor = 123_456 });
        set.EnsureSuccessStatusCode();

        var athlete = await AthleteClientAsync(email);
        var purchase = await SelectAsync(athlete, optionId);

        // An agreed price, not a starting point: no loyalty discount on top, and no rounding.
        Assert.Equal(123_456, purchase.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public async Task Editing_the_option_afterwards_does_not_change_an_existing_purchase()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(
            admin, "Repriced 8", priceMinor: 400_000, sessions: 8,
            features: ["Weekly video call"]);
        var optionId = option.GetProperty("id").GetGuid();

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchase = await SelectAsync(athlete, optionId);
        var purchaseId = purchase.GetProperty("id").GetGuid();

        // The coach renames, reprices and re-features the catalogue entry after the athlete chose.
        var edit = await admin.PutAsJsonAsync($"/api/v1/package-options/{optionId}", new
        {
            name = "Repriced 8 (new)",
            sessions = 12,
            defaultPriceMinor = 900_000,
            features = new[] { "Something else entirely" },
            version = option.GetProperty("version").GetInt32()
        });
        edit.EnsureSuccessStatusCode();

        var reread = await BodyAsync(await admin.GetAsync($"/api/v1/purchases/{purchaseId}"));

        Assert.Equal("Repriced 8", reread.GetProperty("packageName").GetString());
        Assert.Equal(8, reread.GetProperty("sessionCount").GetInt32());
        Assert.Equal(400_000, reread.GetProperty("priceMinor").GetInt64());
        Assert.Equal(
            ["Weekly video call"],
            reread.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());

        // ...and the package created from it carries the snapshot, not today's catalogue.
        var paid = await BodyAsync(await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null));
        var package = paid.GetProperty("package");

        Assert.Equal("Repriced 8", package.GetProperty("name").GetString());
        Assert.Equal(8, package.GetProperty("totalSessions").GetInt32());
        Assert.Equal(400_000, package.GetProperty("pricePaidMinor").GetInt64());
    }

    [Fact]
    public async Task The_price_cannot_be_sent_by_the_client()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Unbribable 8", priceMinor: 400_000);

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        // Extra properties are ignored rather than honoured. An athlete who could name a price
        // could name a different one from the one they were quoted.
        var response = await athlete.PostAsJsonAsync("/api/v1/me/purchases", new
        {
            packageOptionId = option.GetProperty("id").GetGuid(),
            priceMinor = 1,
            sessionCount = 999,
            packageName = "Free stuff"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await BodyAsync(response);

        Assert.Equal(400_000, purchase.GetProperty("priceMinor").GetInt64());
        Assert.Equal(8, purchase.GetProperty("sessionCount").GetInt32());
        Assert.Equal("Unbribable 8", purchase.GetProperty("packageName").GetString());
    }

    // --- replacing a pending selection --------------------------------------

    [Fact]
    public async Task Selecting_a_different_option_replaces_the_pending_purchase()
    {
        var admin = await AdminClientAsync();
        var wrong = await CreateOptionAsync(admin, "Wrong choice 8", priceMinor: 400_000, sessions: 8);
        var right = await CreateOptionAsync(admin, "Right choice 12", priceMinor: 600_000, sessions: 12);

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var first = await SelectAsync(athlete, wrong.GetProperty("id").GetGuid());

        // 200, not 201: the athlete still has exactly one pending request, and it keeps its id.
        var second = await SelectAsync(
            athlete, right.GetProperty("id").GetGuid(), HttpStatusCode.OK);

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Right choice 12", second.GetProperty("packageName").GetString());
        Assert.Equal(12, second.GetProperty("sessionCount").GetInt32());
        Assert.Equal(600_000, second.GetProperty("priceMinor").GetInt64());
        Assert.Equal("Pending", second.GetProperty("status").GetString());

        var pending = await factory.QueryAsync(db => db.PackagePurchases
            .CountAsync(x => x.AthleteUserId == userId
                             && x.Status == PurchasePaymentStatus.Pending));

        Assert.Equal(1, pending);
    }

    [Fact]
    public async Task The_athletes_current_purchase_is_their_pending_one()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Current 8");

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var missing = await athlete.GetAsync("/api/v1/me/purchases/current");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("PURCHASE_NOT_FOUND", ErrorCode(await BodyAsync(missing)));

        var created = await SelectAsync(athlete, option.GetProperty("id").GetGuid());
        var current = await BodyAsync(await athlete.GetAsync("/api/v1/me/purchases/current"));

        Assert.Equal(created.GetProperty("id").GetGuid(), current.GetProperty("id").GetGuid());
        Assert.Equal("Pending", current.GetProperty("status").GetString());
    }

    // --- the Pending to Paid transition -------------------------------------

    [Fact]
    public async Task Marking_paid_creates_the_package_and_records_who_confirmed_it()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Confirmed 8", priceMinor: 400_000, sessions: 8);

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, option.GetProperty("id").GetGuid()))
            .GetProperty("id").GetGuid();

        var response = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyAsync(response);
        var purchase = body.GetProperty("purchase");
        var package = body.GetProperty("package");

        Assert.False(body.GetProperty("alreadyPaid").GetBoolean());
        Assert.Equal("Paid", purchase.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("paidAtUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("paidByUserId").ValueKind);

        // The purchase points at the package it produced, and the package is real and active.
        Assert.Equal(
            package.GetProperty("id").GetGuid(),
            purchase.GetProperty("purchasedPackageId").GetGuid());
        Assert.Equal("Active", package.GetProperty("status").GetString());
        Assert.Equal(8, package.GetProperty("totalSessions").GetInt32());
        Assert.Equal(8, package.GetProperty("remainingSessions").GetInt32());
        Assert.Equal(400_000, package.GetProperty("pricePaidMinor").GetInt64());

        // The Admin who confirmed it, from the token - not from the request.
        var adminId = await factory.QueryAsync(db => db.PackagePurchases
            .Where(x => x.Id == purchaseId).Select(x => x.PaidByUserId).SingleAsync());
        Assert.NotNull(adminId);

        // And the athlete now sees it as their active package.
        var mine = await BodyAsync(await athlete.GetAsync("/api/v1/me/package"));
        Assert.Equal(package.GetProperty("id").GetGuid(), mine.GetProperty("id").GetGuid());
        Assert.Equal(userId, (await BodyAsync(
            await athlete.GetAsync("/api/v1/me/purchases/current"))).GetProperty("athleteUserId").GetGuid());
    }

    [Fact]
    public async Task Marking_paid_twice_returns_the_same_package_and_never_creates_a_second()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Repeat 8");

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, option.GetProperty("id").GetGuid()))
            .GetProperty("id").GetGuid();

        var first = await BodyAsync(await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null));

        var repeat = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var second = await BodyAsync(repeat);

        Assert.False(first.GetProperty("alreadyPaid").GetBoolean());
        Assert.True(second.GetProperty("alreadyPaid").GetBoolean());

        Assert.Equal(
            first.GetProperty("package").GetProperty("id").GetGuid(),
            second.GetProperty("package").GetProperty("id").GetGuid());

        var packages = await factory.QueryAsync(db => db.PurchasedPackages
            .CountAsync(x => db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId)));

        Assert.Equal(1, packages);
    }

    [Fact]
    public async Task Concurrent_mark_paid_requests_produce_exactly_one_package()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Race 8");

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, option.GetProperty("id").GetGuid()))
            .GetProperty("id").GetGuid();

        // The double-tap, for real: eight simultaneous confirmations of the same purchase.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(BodyAsync));

        // Exactly one did the work; the rest were told it had already happened.
        Assert.Single(bodies, b => !b.GetProperty("alreadyPaid").GetBoolean());

        // Every one of them named the same package.
        var packageIds = bodies
            .Select(b => b.GetProperty("package").GetProperty("id").GetGuid())
            .Distinct()
            .ToArray();

        Assert.Single(packageIds);

        var packages = await factory.QueryAsync(db => db.PurchasedPackages
            .CountAsync(x => db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId)));

        Assert.Equal(1, packages);
    }

    [Fact]
    public async Task Marking_an_unknown_purchase_paid_is_404()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsync($"/api/v1/purchases/{Guid.NewGuid()}/mark-paid", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PURCHASE_NOT_FOUND", ErrorCode(await BodyAsync(response)));
    }

    [Fact]
    public async Task There_is_no_way_to_return_a_paid_purchase_to_pending()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "One way 8");

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, option.GetProperty("id").GetGuid()))
            .GetProperty("id").GetGuid();

        await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);

        // Pending -> Paid is the only transition the API has. There is no cancel, no reopen and
        // no unpay: a route for any of them does not exist, which is the strongest form of "not
        // allowed" there is.
        foreach (var path in new[] { "mark-pending", "unpay", "cancel", "reopen" })
        {
            var response = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/{path}", null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var still = await BodyAsync(await admin.GetAsync($"/api/v1/purchases/{purchaseId}"));
        Assert.Equal("Paid", still.GetProperty("status").GetString());
    }

    // --- BR-03, at selection and again at payment ---------------------------

    [Fact]
    public async Task Selecting_is_refused_while_an_active_package_exists()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Blocked 8");
        var optionId = option.GetProperty("id").GetGuid();

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var sold = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{userId}/packages", new { packageOptionId = optionId });
        sold.EnsureSuccessStatusCode();

        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_PACKAGE_EXISTS", ErrorCode(await BodyAsync(response)));
    }

    [Fact]
    public async Task Marking_paid_conflicts_and_leaves_the_purchase_pending_when_a_package_appeared()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Late conflict 8");
        var optionId = option.GetProperty("id").GetGuid();

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, optionId)).GetProperty("id").GetGuid();

        // The coach sells them a package directly while their request is still pending - the
        // window this recheck exists for.
        var sold = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{userId}/packages", new { packageOptionId = optionId });
        sold.EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_PACKAGE_EXISTS", ErrorCode(await BodyAsync(response)));

        // Nothing half-done: still Pending, still no package of its own, and confirmable later.
        var after = await BodyAsync(await admin.GetAsync($"/api/v1/purchases/{purchaseId}"));
        Assert.Equal("Pending", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("purchasedPackageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("paidAtUtc").ValueKind);

        var packages = await factory.QueryAsync(db => db.PurchasedPackages
            .CountAsync(x => db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId)));

        Assert.Equal(1, packages);
    }

    [Fact]
    public async Task A_purchase_can_be_confirmed_once_the_blocking_package_is_closed()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Unblocked 8");
        var optionId = option.GetProperty("id").GetGuid();

        var (userId, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, optionId)).GetProperty("id").GetGuid();

        var sold = await BodyAsync(await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{userId}/packages", new { packageOptionId = optionId }));

        var blocked = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var closed = await admin.PostAsync(
            $"/api/v1/packages/{sold.GetProperty("id").GetGuid()}/close", null);
        closed.EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Paid", (await BodyAsync(response)).GetProperty("purchase").GetProperty("status").GetString());
    }

    // --- the Admin direct sale still records payment history -----------------

    [Fact]
    public async Task An_admin_recorded_package_creates_a_paid_purchase_beside_it()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(
            admin, "Off app 8", priceMinor: 400_000, features: ["Weekly video call", "Session notes"]);

        var (userId, _) = await factory.NewAthleteAsync();

        var package = await BodyAsync(await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{userId}/packages",
            new { packageOptionId = option.GetProperty("id").GetGuid() }));

        var purchases = await ItemsAsync(
            await admin.GetAsync($"/api/v1/purchases?athleteId={userId}"));

        var purchase = Assert.Single(purchases.EnumerateArray().ToArray());

        Assert.Equal("Paid", purchase.GetProperty("status").GetString());
        Assert.Equal("AdminDirect", purchase.GetProperty("origin").GetString());
        Assert.Equal(400_000, purchase.GetProperty("priceMinor").GetInt64());
        Assert.Equal(
            package.GetProperty("id").GetGuid(),
            purchase.GetProperty("purchasedPackageId").GetGuid());
        Assert.Equal(
            ["Weekly video call", "Session notes"],
            purchase.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());
    }

    // --- the Admin list ------------------------------------------------------

    [Fact]
    public async Task The_admin_list_filters_by_status_and_by_athlete()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Listed 8");
        var optionId = option.GetProperty("id").GetGuid();

        var (pendingUserId, pendingEmail) = await factory.NewAthleteAsync();
        var (paidUserId, paidEmail) = await factory.NewAthleteAsync();

        await SelectAsync(await AthleteClientAsync(pendingEmail), optionId);

        var toPay = (await SelectAsync(await AthleteClientAsync(paidEmail), optionId))
            .GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/purchases/{toPay}/mark-paid", null);

        var pending = await ItemsAsync(await admin.GetAsync("/api/v1/purchases?status=Pending"));
        Assert.Contains(pending.EnumerateArray(),
            x => x.GetProperty("athleteUserId").GetGuid() == pendingUserId);
        Assert.All(pending.EnumerateArray(),
            x => Assert.Equal("Pending", x.GetProperty("status").GetString()));

        var paid = await ItemsAsync(await admin.GetAsync("/api/v1/purchases?status=Paid"));
        Assert.Contains(paid.EnumerateArray(),
            x => x.GetProperty("athleteUserId").GetGuid() == paidUserId);
        Assert.All(paid.EnumerateArray(),
            x => Assert.Equal("Paid", x.GetProperty("status").GetString()));

        var forOne = await ItemsAsync(await admin.GetAsync($"/api/v1/purchases?athleteId={pendingUserId}"));
        Assert.All(forOne.EnumerateArray(),
            x => Assert.Equal(pendingUserId, x.GetProperty("athleteUserId").GetGuid()));
    }

    [Fact]
    public async Task Listing_for_an_unknown_athlete_is_404_not_an_empty_list()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/purchases?athleteId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND", ErrorCode(await BodyAsync(response)));
    }

    // --- authorization -------------------------------------------------------

    [Fact]
    public async Task An_athlete_cannot_reach_any_admin_purchase_endpoint()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Forbidden 8");

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);
        var purchaseId = (await SelectAsync(athlete, option.GetProperty("id").GetGuid()))
            .GetProperty("id").GetGuid();

        // Their own purchase, through the Admin routes. Still forbidden - listing every purchase
        // and confirming payment are the coach's, and an athlete must never mark their own paid.
        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.GetAsync("/api/v1/purchases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.GetAsync($"/api/v1/purchases/{purchaseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null)).StatusCode);

        // And it is still Pending afterwards.
        var after = await BodyAsync(await admin.GetAsync($"/api/v1/purchases/{purchaseId}"));
        Assert.Equal("Pending", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_admin_cannot_create_a_purchase_for_themselves()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Not for admins 8");

        var create = await admin.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = option.GetProperty("id").GetGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/me/purchases/current")).StatusCode);
    }

    [Fact]
    public async Task An_athlete_never_sees_another_athletes_purchase()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Private 8");
        var optionId = option.GetProperty("id").GetGuid();

        var (_, mineEmail) = await factory.NewAthleteAsync();
        var (_, theirsEmail) = await factory.NewAthleteAsync();

        var theirs = await SelectAsync(await AthleteClientAsync(theirsEmail), optionId);
        var mine = await SelectAsync(await AthleteClientAsync(mineEmail), optionId);

        Assert.NotEqual(theirs.GetProperty("id").GetGuid(), mine.GetProperty("id").GetGuid());

        // /me/purchases/current is scoped to the token and takes no athlete id at all, so the
        // only purchase reachable is the caller's own.
        var current = await BodyAsync(
            await (await AthleteClientAsync(mineEmail)).GetAsync("/api/v1/me/purchases/current"));

        Assert.Equal(mine.GetProperty("id").GetGuid(), current.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Every_purchase_endpoint_requires_a_token()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/purchases")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/me/purchases/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/payments/instapay-instructions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(
            $"/api/v1/purchases/{Guid.NewGuid()}/mark-paid", null)).StatusCode);
    }

    // --- selecting something that cannot be sold -----------------------------

    [Fact]
    public async Task An_archived_option_cannot_be_selected()
    {
        var admin = await AdminClientAsync();
        var option = await CreateOptionAsync(admin, "Withdrawn 8");
        var optionId = option.GetProperty("id").GetGuid();

        var archive = await admin.PostAsJsonAsync(
            $"/api/v1/package-options/{optionId}/archive",
            new { version = option.GetProperty("version").GetInt32() });
        archive.EnsureSuccessStatusCode();

        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PACKAGE_OPTION_ARCHIVED", ErrorCode(await BodyAsync(response)));
    }

    [Fact]
    public async Task An_unknown_option_is_404_and_a_missing_one_is_a_validation_failure()
    {
        var (_, email) = await factory.NewAthleteAsync();
        var athlete = await AthleteClientAsync(email);

        var unknown = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("PACKAGE_OPTION_NOT_FOUND", ErrorCode(await BodyAsync(unknown)));

        var empty = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("VALIDATION_FAILED", ErrorCode(await BodyAsync(empty)));
    }

    // --- InstaPay, unconfigured ----------------------------------------------

    [Fact]
    public async Task Payment_instructions_are_503_until_they_are_configured()
    {
        var (_, email) = await factory.NewAthleteAsync();

        foreach (var client in new[] { await AdminClientAsync(), await AthleteClientAsync(email) })
        {
            var response = await client.GetAsync("/api/v1/payments/instapay-instructions");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("INSTAPAY_NOT_CONFIGURED", ErrorCode(await BodyAsync(response)));
        }
    }
}

/// <summary>
/// The configured half of <c>GET /payments/instapay-instructions</c>. Its own fixture because
/// the values are supplied at host start, and a test cannot change them afterwards.
/// </summary>
public sealed class PaymentInstructionsTests(InstaPayApiFactory factory)
    : IClassFixture<InstaPayApiFactory>
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

    [Fact]
    public async Task Configured_instructions_are_returned_to_both_roles()
    {
        var (_, email) = await factory.NewAthleteAsync();

        var clients = new[]
        {
            await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword),
            await SignInAsync(email, AthleteApiFactory.AthletePassword)
        };

        foreach (var client in clients)
        {
            var response = await client.GetAsync("/api/v1/payments/instapay-instructions");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(InstaPayApiFactory.PaymentUrl, body.GetProperty("paymentUrl").GetString());
            Assert.Equal(InstaPayApiFactory.QrImageUrl, body.GetProperty("qrImageUrl").GetString());
            Assert.Equal(InstaPayApiFactory.RecipientName, body.GetProperty("recipientName").GetString());
            Assert.Equal(InstaPayApiFactory.RecipientHandle, body.GetProperty("recipientHandle").GetString());

            // Ordered steps, in the order configured.
            Assert.Equal(
                [
                    "Open InstaPay and scan the QR code.",
                    "Send the exact amount shown on your purchase.",
                    "Your coach confirms it once the transfer arrives."
                ],
                body.GetProperty("instructions").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }
    }
}
