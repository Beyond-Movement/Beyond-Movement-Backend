using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Packages.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests count an athlete's packages and purchases, so nothing else may
/// be creating them.
/// </summary>
public sealed class ZeroPricePurchaseApiFactory : PurchaseApiFactory;

/// <summary>
/// A package that costs nothing completes itself.
/// <para>
/// The product has no gateway: an athlete selects, pays outside the platform, and the Admin
/// confirms receipt. That works for every price except zero, where there is no transfer to make
/// and therefore nothing for the Admin to confirm — so the purchase used to sit Pending forever
/// and the athlete never received the package their coach had given them.
/// </para>
/// <para>
/// <b>Only the price decides.</b> Everything else about the flow is untouched, which is half of
/// what these tests are for: a purchase of one piastre still goes through InstaPay and still
/// waits for the coach.
/// </para>
/// </summary>
public sealed class ZeroPricePurchaseTests(ZeroPricePurchaseApiFactory factory)
    : IClassFixture<ZeroPricePurchaseApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    // --- clients -------------------------------------------------------------

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

    // --- arrangement ---------------------------------------------------------

    private static async Task<Guid> CreateOptionAsync(
        HttpClient admin, string name, long priceMinor, int sessions = 8, object[]? features = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name,
            sessions,
            defaultPriceMinor = priceMinor,
            features = features ?? Features.Open("Weekly video call", "Session notes")
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"option {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SetCustomPriceAsync(
        HttpClient admin, Guid athleteUserId, Guid optionId, long priceMinor)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteUserId}/custom-prices/{optionId}", new { priceMinor });

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"custom price {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> SelectAsync(
        HttpClient athlete, Guid optionId, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        if (response.StatusCode != expected)
            Assert.Fail($"select {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Asserts the purchase reached the same final state a confirmed payment produces, and that
    /// it claims no payment that never happened.
    /// </summary>
    private static Guid AssertCompleted(JsonElement purchase)
    {
        Assert.Equal("Paid", purchase.GetProperty("status").GetString());

        // Who started it is not affected by what it cost.
        Assert.Equal("Athlete", purchase.GetProperty("origin").GetString());

        Assert.Equal(0, purchase.GetProperty("priceMinor").GetInt64());

        // CK_PackagePurchases_PaidConsistency: a Paid row carries the moment and the package.
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("paidAtUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("purchasedPackageId").ValueKind);

        // ...and deliberately not a confirming user. Nobody confirmed anything, and writing the
        // athlete's id here would be a claim no human made.
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("paidByUserId").ValueKind);

        return purchase.GetProperty("purchasedPackageId").GetGuid();
    }

    private static void AssertPending(JsonElement purchase)
    {
        Assert.Equal("Pending", purchase.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("purchasedPackageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("paidAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("paidByUserId").ValueKind);
    }

    // --- the three ways a price legitimately reaches zero ---------------------

    [Fact]
    public async Task A_custom_price_of_zero_completes_the_purchase_and_activates_the_package()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – custom", priceMinor: 400_000);

        // The coach comps this athlete specifically. The option still costs everyone else money.
        await SetCustomPriceAsync(admin, athleteId, optionId, priceMinor: 0);

        var athlete = await AthleteClientAsync(email);
        var purchase = await SelectAsync(athlete, optionId);

        var packageId = AssertCompleted(purchase);

        // The same final state a confirmed payment produces: the package exists and is active.
        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal("Active", package.GetProperty("status").GetString());
        Assert.Equal(8, package.GetProperty("totalSessions").GetInt32());
        Assert.Equal(0, package.GetProperty("usedSessions").GetInt32());
        Assert.Equal(8, package.GetProperty("remainingSessions").GetInt32());
        Assert.Equal(0, package.GetProperty("pricePaidMinor").GetInt64());

        // And the athlete can see it immediately, with no Admin action in between.
        var mine = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/package");
        Assert.Equal(packageId, mine.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_default_price_of_zero_completes_the_purchase()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();

        // No override anywhere: the option itself is free, which the validator has always
        // allowed. PricingSource here is Default, not Custom, and it reaches zero all the same.
        var optionId = await CreateOptionAsync(admin, "Zero – default", priceMinor: 0);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);

        AssertCompleted(purchase);
    }

    [Fact]
    public async Task A_loyalty_discount_applied_to_a_free_option_still_completes_the_purchase()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync(isLoyal: true);
        var optionId = await CreateOptionAsync(admin, "Zero – loyal", priceMinor: 0);

        // 15% off nothing is nothing. PricingSource is Loyalty, and the rule is the price rather
        // than which rule produced it.
        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);

        AssertCompleted(purchase);
    }

    // --- and every price that is not zero is untouched ------------------------

    [Fact]
    public async Task A_custom_price_above_zero_still_waits_for_the_coach()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Pending – custom", priceMinor: 400_000);

        await SetCustomPriceAsync(admin, athleteId, optionId, priceMinor: 100_000);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);

        AssertPending(purchase);
        Assert.Equal(100_000, purchase.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public async Task A_default_price_above_zero_still_waits_for_the_coach()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Pending – default", priceMinor: 400_000);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);

        AssertPending(purchase);
        Assert.Equal(400_000, purchase.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public async Task A_loyalty_discounted_price_above_zero_still_waits_for_the_coach()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync(isLoyal: true);
        var optionId = await CreateOptionAsync(admin, "Pending – loyal", priceMinor: 400_000);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);

        AssertPending(purchase);

        // 15% off 4,000.00 EGP, and emphatically not zero.
        Assert.Equal(340_000, purchase.GetProperty("priceMinor").GetInt64());
    }

    /// <summary>
    /// One piastre is not zero. The boundary is worth its own test, because "free" is the kind of
    /// condition somebody later writes as <c>&lt;= 0</c> or as a rounding tolerance.
    /// </summary>
    [Fact]
    public async Task A_price_of_one_piastre_still_waits_for_the_coach()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Pending – one piastre", priceMinor: 1);

        AssertPending(await SelectAsync(await AthleteClientAsync(email), optionId));
    }

    // --- the snapshot is still the snapshot -----------------------------------

    [Fact]
    public async Task A_completed_free_purchase_keeps_the_whole_snapshot()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();

        var optionId = await CreateOptionAsync(admin, "Zero – snapshot", priceMinor: 0,
            sessions: 12, features: Features.WithObservations());

        var athlete = await AthleteClientAsync(email);
        var purchase = await SelectAsync(athlete, optionId);
        var packageId = AssertCompleted(purchase);

        // Built from the stored snapshot by the same code a confirmed payment runs, so the name,
        // the session count and the feature lines are what the athlete was shown.
        Assert.Equal("Zero – snapshot", purchase.GetProperty("packageName").GetString());
        Assert.Equal(12, purchase.GetProperty("sessionCount").GetInt32());

        Assert.Equal(
            ["Weekly video call", "Observations"],
            purchase.GetProperty("features").EnumerateArray()
                .Select(x => x.GetProperty("text").GetString()!).ToArray());

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal("Zero – snapshot", package.GetProperty("name").GetString());
        Assert.Equal(12, package.GetProperty("totalSessions").GetInt32());

        // The recognised feature codes carry across too, so a free package grants what it says.
        Assert.Equal(["Observations"],
            package.GetProperty("includedFeatures").EnumerateArray()
                .Select(x => x.GetString()!).ToArray());
    }

    // --- BR-03 and repeats ----------------------------------------------------

    [Fact]
    public async Task An_athlete_with_an_active_package_still_cannot_select_a_free_one()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();

        var paidOption = await CreateOptionAsync(admin, "Zero – BR-03 held", priceMinor: 400_000);
        var freeOption = await CreateOptionAsync(admin, "Zero – BR-03 free", priceMinor: 0);

        // The Admin sells them one directly, so an active package exists before the athlete tries.
        var sold = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/packages", new { packageOptionId = paidOption });
        sold.EnsureSuccessStatusCode();

        var athlete = await AthleteClientAsync(email);

        var response = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = freeOption });

        // BR-03 is checked before anything is written, so a free package cannot slip past the
        // one-active-package rule that a paid one obeys.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_PACKAGE_EXISTS",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        var count = await ActivePackageCountAsync(email);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Selecting_a_free_package_twice_does_not_produce_a_second_package()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – repeated", priceMinor: 0);

        var athlete = await AthleteClientAsync(email);

        var first = await SelectAsync(athlete, optionId);
        var packageId = AssertCompleted(first);

        // The second attempt finds the athlete already holding the package the first created.
        var second = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("ACTIVE_PACKAGE_EXISTS",
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        Assert.Equal(1, await PackageCountAsync(email));
        Assert.Equal(1, await PurchaseCountAsync(email));

        // And the one package is the one the first request reported.
        var mine = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/package");
        Assert.Equal(packageId, mine.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Concurrent_selections_of_a_free_package_produce_exactly_one_package()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – concurrent", priceMinor: 0);

        var athlete = await AthleteClientAsync(email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            athlete.PostAsJsonAsync("/api/v1/me/purchases", new { packageOptionId = optionId })));

        // Whatever the losers are told, the database is what matters: the partial unique index on
        // one active package per athlete is the guarantee, not the order the requests arrived in.
        Assert.Equal(1, await PackageCountAsync(email));

        Assert.Contains(responses,
            x => x.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
    }

    /// <summary>
    /// The athlete picked a package they had to pay for, then changed their mind to one the coach
    /// had comped. Revising re-prices under today's rules, so the revised purchase is free and
    /// completes — from the same pending row, which keeps its id.
    /// </summary>
    [Fact]
    public async Task Revising_a_pending_purchase_down_to_zero_completes_it()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();

        var paidOption = await CreateOptionAsync(admin, "Zero – revised from", priceMinor: 400_000);
        var freeOption = await CreateOptionAsync(admin, "Zero – revised to", priceMinor: 0);

        var athlete = await AthleteClientAsync(email);

        var pending = await SelectAsync(athlete, paidOption);
        AssertPending(pending);

        // 200 rather than 201: this revises the request they already had.
        var revised = await SelectAsync(athlete, freeOption, HttpStatusCode.OK);

        Assert.Equal(pending.GetProperty("id").GetGuid(), revised.GetProperty("id").GetGuid());
        AssertCompleted(revised);

        Assert.Equal(1, await PurchaseCountAsync(email));
    }

    // --- what the other endpoints then say ------------------------------------

    [Fact]
    public async Task The_athletes_current_purchase_reports_the_completed_state()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – current", priceMinor: 0);

        var athlete = await AthleteClientAsync(email);
        var packageId = AssertCompleted(await SelectAsync(athlete, optionId));

        // No pending request is left behind for this to return instead.
        var current = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/purchases/current");

        Assert.Equal("Paid", current.GetProperty("status").GetString());
        Assert.Equal(packageId, current.GetProperty("purchasedPackageId").GetGuid());
    }

    [Fact]
    public async Task A_completed_free_purchase_appears_in_the_admins_paid_list_and_not_the_queue()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – admin list", priceMinor: 0);

        await SelectAsync(await AthleteClientAsync(email), optionId);

        var pending = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/purchases?status=Pending&athleteId={athleteId}");

        // The coach's queue is for purchases needing a decision. This one needs none.
        Assert.Empty(pending.GetProperty("items").EnumerateArray());

        var paid = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/purchases?status=Paid&athleteId={athleteId}");

        var row = paid.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(0, row.GetProperty("priceMinor").GetInt64());
        Assert.Equal("Athlete", row.GetProperty("origin").GetString());
    }

    /// <summary>
    /// Confirming it again is harmless, which matters because the Admin may still have the row on
    /// screen from before it completed. It is the ordinary idempotent path — the same package
    /// comes back, and no second one is made.
    /// </summary>
    [Fact]
    public async Task An_admin_marking_an_already_completed_free_purchase_paid_changes_nothing()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – re-confirmed", priceMinor: 0);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);
        var packageId = AssertCompleted(purchase);
        var purchaseId = purchase.GetProperty("id").GetGuid();

        var again = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        again.EnsureSuccessStatusCode();

        var body = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("alreadyPaid").GetBoolean());
        Assert.Equal(packageId, body.GetProperty("package").GetProperty("id").GetGuid());

        // Still nobody's confirmation: re-confirming does not retrospectively claim one.
        Assert.Equal(JsonValueKind.Null,
            body.GetProperty("purchase").GetProperty("paidByUserId").ValueKind);

        Assert.Equal(1, await PackageCountAsync(email));
    }

    // --- the audit trail ------------------------------------------------------

    [Fact]
    public async Task Completing_a_free_purchase_is_audited_as_its_own_event()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – audited", priceMinor: 0);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);
        var purchaseId = purchase.GetProperty("id").GetGuid();

        var entry = await factory.QueryAsync(db => db.AuditLogs.AsNoTracking()
            .Where(x => x.Details!.Contains(purchaseId.ToString())
                        && x.Action != "PackagePurchaseRequested")
            .SingleAsync());

        // A distinct action, so payment history can tell "an Admin confirmed money arrived" from
        // "there was nothing to confirm" rather than inferring it from a zero.
        Assert.Equal("PackagePurchaseAutoCompleted", entry.Action);

        // No actor, for the same reason paidByUserId has none.
        Assert.Null(entry.ActorUserId);
        Assert.Contains("priceMinor=0", entry.Details);
    }

    [Fact]
    public async Task Confirming_a_paid_purchase_is_still_audited_as_a_payment_by_the_admin()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – paid audit", priceMinor: 400_000);

        var purchase = await SelectAsync(await AthleteClientAsync(email), optionId);
        var purchaseId = purchase.GetProperty("id").GetGuid();

        (await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null))
            .EnsureSuccessStatusCode();

        var entry = await factory.QueryAsync(db => db.AuditLogs.AsNoTracking()
            .Where(x => x.Details!.Contains(purchaseId.ToString())
                        && x.Action == "PackagePurchasePaid")
            .SingleAsync());

        // The existing event is unchanged and still names the Admin who confirmed.
        Assert.NotNull(entry.ActorUserId);
    }

    // --- the Admin's own sales are untouched ----------------------------------

    [Fact]
    public async Task An_admin_recorded_free_package_still_records_the_admin_who_sold_it()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Zero – admin direct", priceMinor: 0);

        var sold = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/packages", new { packageOptionId = optionId });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var purchase = (await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/purchases?athleteId={athleteId}")).GetProperty("items").EnumerateArray().Single();

        // Recording it IS the confirmation, so this path was already immediate. Unlike the
        // athlete's, it has a real confirming Admin and must keep naming them.
        Assert.Equal("Paid", purchase.GetProperty("status").GetString());
        Assert.Equal("AdminDirect", purchase.GetProperty("origin").GetString());
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("paidByUserId").ValueKind);
        Assert.Equal(0, purchase.GetProperty("priceMinor").GetInt64());

        Assert.Equal(1, await PackageCountAsync(email));
    }

    // --- database helpers -----------------------------------------------------

    private Task<int> PackageCountAsync(string email) =>
        factory.QueryAsync(db =>
            (from user in db.Users
             join profile in db.AthleteProfiles on user.Id equals profile.UserId
             join package in db.PurchasedPackages on profile.Id equals package.AthleteProfileId
             where user.Email == email
             select package.Id).CountAsync());

    private Task<int> ActivePackageCountAsync(string email) =>
        factory.QueryAsync(db =>
            (from user in db.Users
             join profile in db.AthleteProfiles on user.Id equals profile.UserId
             join package in db.PurchasedPackages on profile.Id equals package.AthleteProfileId
             where user.Email == email && package.Status == PurchasedPackageStatus.Active
             select package.Id).CountAsync());

    private Task<int> PurchaseCountAsync(string email) =>
        factory.QueryAsync(db =>
            (from user in db.Users
             join purchase in db.PackagePurchases on user.Id equals purchase.AthleteUserId
             where user.Email == email
             select purchase.Id).CountAsync());
}
