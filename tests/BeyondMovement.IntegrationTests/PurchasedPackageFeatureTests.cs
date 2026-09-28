using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests edit and archive catalogue options and count an athlete's
/// packages, so nothing else may be touching them.
/// </summary>
public sealed class PurchasedPackageFeatureApiFactory : PurchaseApiFactory;

/// <summary>
/// The feature lines on <c>PurchasedPackageResponse</c> — what the athlete read, as they read it.
/// <para>
/// <c>includedFeatures</c> is the machine-readable eligibility list and carries no text; a rule
/// must never be decided by comparing words the coach is free to reword. <c>features</c> is the
/// card, snapshotted at purchase. The point of these tests is that the second is a <b>snapshot</b>
/// and not a lookup: editing, rewording or archiving the catalogue entry afterwards changes
/// nothing about a package somebody already owns.
/// </para>
/// </summary>
public sealed class PurchasedPackageFeatureTests(PurchasedPackageFeatureApiFactory factory)
    : IClassFixture<PurchasedPackageFeatureApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    /// <summary>
    /// Deliberately NOT the enum member's name. If anything ever derived the label from the code,
    /// this text would come back as "Observations" and every test below would say so.
    /// </summary>
    private const string ObservationLine = "Observation Sessions";

    private const string OrdinaryLine = "Competition preparation";

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

    /// <summary>An option whose card mixes one recognised line with one ordinary one.</summary>
    private static async Task<Guid> CreateOptionAsync(
        HttpClient admin, string name, object[]? features = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name,
            sessions = 8,
            defaultPriceMinor = 400_000L,
            features = features ?? Features.List(
                Features.One(ObservationLine, Features.Observations),
                Features.One(OrdinaryLine))
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"option {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SellAsync(HttpClient admin, Guid athleteUserId, Guid optionId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/athletes/{athleteUserId}/packages", new { packageOptionId = optionId });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"sell {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // --- reading -------------------------------------------------------------

    /// <summary>The (text, code) pairs of a package response, in order.</summary>
    private static (string Text, string? Code)[] FeaturesOf(JsonElement package) =>
        [.. package.GetProperty("features").EnumerateArray().Select(x => (
            x.GetProperty("text").GetString()!,
            x.GetProperty("code").ValueKind == JsonValueKind.Null
                ? null
                : x.GetProperty("code").GetString()))];

    /// <summary>
    /// Non-null by construction: includedFeatures is a list of enum names, never a list with
    /// holes in it.
    /// </summary>
    private static string[] CodesOf(JsonElement package) =>
        [.. package.GetProperty("includedFeatures").EnumerateArray().Select(x => x.GetString()!)];

    private static readonly (string Text, string? Code)[] ExpectedCard =
        [(ObservationLine, "Observations"), (OrdinaryLine, null)];

    // --- the shape -----------------------------------------------------------

    /// <summary>
    /// Stated exactly. The pair of feature fields is the point: one for deciding, one for drawing,
    /// and neither derived from the other.
    /// </summary>
    [Fact]
    public async Task A_purchased_package_carries_both_feature_fields_and_nothing_else_changed()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – shape");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(
            ["id", "athleteProfileId", "packageOptionId", "name", "totalSessions", "usedSessions",
             "remainingSessions", "includedFeatures", "features", "pricePaidMinor", "currency",
             "startDate", "endDate", "status", "notes", "createdAtUtc", "updatedAtUtc"],
            package.EnumerateObject().Select(p => p.Name).ToArray());

        // Each line is exactly { text, code } and nothing more.
        foreach (var line in package.GetProperty("features").EnumerateArray())
            Assert.Equal(["text", "code"], line.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Features_carry_the_text_and_the_code_in_the_order_they_were_written()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – order");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(ExpectedCard, FeaturesOf(package));

        // The label is the coach's text, NOT the enum member's name. If display text were ever
        // derived from the code this would read "Observations".
        Assert.Equal(ObservationLine, FeaturesOf(package)[0].Text);
        Assert.NotEqual("Observations", FeaturesOf(package)[0].Text);
    }

    /// <summary>
    /// The two fields describe the same card and must agree about which lines are recognised -
    /// while staying two different things: codes decide, text draws.
    /// </summary>
    [Fact]
    public async Task IncludedFeatures_is_unchanged_and_agrees_with_the_codes_on_the_lines()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – codes");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(["Observations"], CodesOf(package));

        Assert.Equal(
            CodesOf(package),
            FeaturesOf(package).Where(x => x.Code is not null).Select(x => x.Code!).ToArray());
    }

    [Fact]
    public async Task A_package_whose_card_has_no_recognised_line_reports_no_codes_but_still_has_text()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();

        var optionId = await CreateOptionAsync(admin, "Features – ordinary only",
            Features.Open("Weekly video call", "Session notes"));

        var packageId = await SellAsync(admin, athleteId, optionId);

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Empty(CodesOf(package));
        Assert.Equal(
            [("Weekly video call", null), ("Session notes", null)],
            FeaturesOf(package));
    }

    // --- the snapshot is a snapshot ------------------------------------------

    /// <summary>
    /// The test this whole change exists for. The coach rewords their catalogue entry; what the
    /// athlete bought keeps saying what it said when they bought it.
    /// </summary>
    [Fact]
    public async Task Editing_the_option_afterwards_does_not_change_a_packages_feature_text()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – edited later");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");

        // Reword both lines, drop the recognised code, rename and reprice the option.
        var edited = await admin.PutAsJsonAsync($"/api/v1/package-options/{optionId}", new
        {
            name = "Features – edited later (renamed)",
            sessions = 12,
            defaultPriceMinor = 900_000L,
            features = Features.Open("COMPLETELY DIFFERENT LINE", "AND ANOTHER"),
            version = option.GetProperty("version").GetInt32()
        });

        if (edited.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"edit {(int)edited.StatusCode}: {await edited.Content.ReadAsStringAsync()}");

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        // Word for word what was sold.
        Assert.Equal(ExpectedCard, FeaturesOf(package));

        // And the rest of the snapshot is untouched too, as it always was.
        Assert.Equal(["Observations"], CodesOf(package));
        Assert.Equal("Features – edited later", package.GetProperty("name").GetString());
        Assert.Equal(8, package.GetProperty("totalSessions").GetInt32());
        Assert.Equal(400_000, package.GetProperty("pricePaidMinor").GetInt64());
    }

    [Fact]
    public async Task Archiving_the_option_afterwards_does_not_change_a_packages_feature_text()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – archived later");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");

        var archived = await admin.PostAsJsonAsync($"/api/v1/package-options/{optionId}/archive",
            new { version = option.GetProperty("version").GetInt32() });

        archived.EnsureSuccessStatusCode();

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(ExpectedCard, FeaturesOf(package));
    }

    /// <summary>
    /// Two athletes buy the same option either side of an edit. Their packages disagree, which is
    /// the correct answer: they were sold different cards.
    /// </summary>
    [Fact]
    public async Task Two_packages_from_one_option_keep_the_card_each_was_sold()
    {
        var admin = await AdminClientAsync();
        var (firstAthlete, _) = await factory.NewAthleteAsync();
        var (secondAthlete, _) = await factory.NewAthleteAsync();

        var optionId = await CreateOptionAsync(admin, "Features – two buyers");
        var firstPackage = await SellAsync(admin, firstAthlete, optionId);

        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");

        (await admin.PutAsJsonAsync($"/api/v1/package-options/{optionId}", new
        {
            name = "Features – two buyers",
            sessions = 8,
            defaultPriceMinor = 400_000L,
            features = Features.List(
                Features.One("Observation Sessions, reworded", Features.Observations),
                Features.One("A different second line")),
            version = option.GetProperty("version").GetInt32()
        })).EnsureSuccessStatusCode();

        var secondPackage = await SellAsync(admin, secondAthlete, optionId);

        var first = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{firstPackage}");
        var second = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{secondPackage}");

        Assert.Equal(ExpectedCard, FeaturesOf(first));
        Assert.Equal(
            [("Observation Sessions, reworded", "Observations"), ("A different second line", null)],
            FeaturesOf(second));
    }

    // --- every route that returns a package ----------------------------------

    [Fact]
    public async Task The_active_package_the_detail_and_the_history_all_carry_the_same_card()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – every admin route");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var active = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/packages/active");

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        var history = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/packages");

        var row = history.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == packageId);

        Assert.Equal(ExpectedCard, FeaturesOf(active));
        Assert.Equal(ExpectedCard, FeaturesOf(detail));
        Assert.Equal(ExpectedCard, FeaturesOf(row));
    }

    /// <summary>
    /// The athlete's own routes are the same model as the Admin's, so the mobile app can reuse
    /// one card widget. Asserted as raw JSON equality, which is how the history tests already
    /// pin the pair.
    /// </summary>
    [Fact]
    public async Task The_athlete_sees_the_same_card_as_the_admin()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – athlete routes");
        await SellAsync(admin, athleteId, optionId);

        var athlete = await AthleteClientAsync(email);

        var theirs = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/package");
        var coachs = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/packages/active");

        Assert.Equal(coachs.GetRawText(), theirs.GetRawText());
        Assert.Equal(ExpectedCard, FeaturesOf(theirs));

        var theirHistory = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/packages");
        var coachHistory = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/packages");

        Assert.Equal(coachHistory.GetRawText(), theirHistory.GetRawText());
        Assert.Equal(
            ExpectedCard,
            FeaturesOf(theirHistory.GetProperty("items").EnumerateArray().First()));
    }

    [Fact]
    public async Task Every_row_of_a_paged_history_carries_its_own_card()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();

        var plainOption = await CreateOptionAsync(admin, "Features – paged plain",
            Features.Open("Plain line"));

        var richOption = await CreateOptionAsync(admin, "Features – paged rich");

        // Sold, closed, sold again: two packages with different cards for one athlete.
        var plainPackage = await SellAsync(admin, athleteId, plainOption);
        (await admin.PostAsync($"/api/v1/packages/{plainPackage}/close", null)).EnsureSuccessStatusCode();

        var richPackage = await SellAsync(admin, athleteId, richOption);

        var history = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/packages");

        var rows = history.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(ExpectedCard,
            FeaturesOf(rows.Single(x => x.GetProperty("id").GetGuid() == richPackage)));

        Assert.Equal([("Plain line", null)],
            FeaturesOf(rows.Single(x => x.GetProperty("id").GetGuid() == plainPackage)));
    }

    [Fact]
    public async Task Closing_a_package_returns_it_with_its_card()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – closed");
        var packageId = await SellAsync(admin, athleteId, optionId);

        var closed = await admin.PostAsync($"/api/v1/packages/{packageId}/close", null);
        closed.EnsureSuccessStatusCode();

        var body = await closed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Closed", body.GetProperty("status").GetString());
        Assert.Equal(ExpectedCard, FeaturesOf(body));
    }

    // --- the athlete's own checkout, and attendance --------------------------

    /// <summary>
    /// The other way a package comes into existence: the athlete selects and the Admin confirms.
    /// The card has to survive that route too, and the confirmation response carries it without a
    /// re-read.
    /// </summary>
    [Fact]
    public async Task A_package_created_by_confirming_a_payment_carries_the_card()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – checkout");

        var athlete = await AthleteClientAsync(email);

        var selected = await athlete.PostAsJsonAsync(
            "/api/v1/me/purchases", new { packageOptionId = optionId });

        selected.EnsureSuccessStatusCode();
        var purchaseId = (await selected.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var paid = await admin.PostAsync($"/api/v1/purchases/{purchaseId}/mark-paid", null);
        paid.EnsureSuccessStatusCode();

        var body = await paid.Content.ReadFromJsonAsync<JsonElement>();

        // The package inside the confirmation response, with no second call.
        Assert.Equal(ExpectedCard, FeaturesOf(body.GetProperty("package")));

        // And the same thing when read back afterwards.
        var packageId = body.GetProperty("package").GetProperty("id").GetGuid();
        var reread = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Equal(ExpectedCard, FeaturesOf(reread));
    }

    [Fact]
    public async Task Marking_a_session_attended_returns_the_package_with_its_card()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – attendance");
        await SellAsync(admin, athleteId, optionId);

        var profileId = await factory.QueryAsync(db => db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == athleteId).Select(x => x.Id).SingleAsync());

        var start = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        var observation = await admin.PostAsJsonAsync("/api/v1/sessions/observations", new
        {
            athleteProfileId = profileId,
            startUtc = start,
            endUtc = start.AddMinutes(60),
            locationOrPlatform = "Regional final",
            deductSession = true
        });

        observation.EnsureSuccessStatusCode();
        var sessionId = (await observation.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var attended = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/attend", new { outcome = "Attended" });

        attended.EnsureSuccessStatusCode();
        var body = await attended.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ExpectedCard, FeaturesOf(body.GetProperty("package")));
    }

    // --- historical packages -------------------------------------------------

    /// <summary>
    /// Every package that predates the purchase record was backfilled with an <b>empty</b>
    /// snapshot, deliberately: the catalogue may have been edited since, so copying it then would
    /// have fabricated a card nobody was shown. Those packages must still answer - with an empty
    /// list, which the contract documents - rather than failing or borrowing the catalogue's
    /// current text.
    /// </summary>
    [Fact]
    public async Task A_package_with_no_snapshot_returns_an_empty_card_rather_than_the_catalogues()
    {
        var admin = await AdminClientAsync();
        var (athleteId, email) = await factory.NewAthleteAsync();
        var optionId = await CreateOptionAsync(admin, "Features – legacy");
        var packageId = await SellAsync(admin, athleteId, optionId);

        // Exactly the state the Phase 8 backfill left: a paid purchase with no feature rows.
        await factory.QueryAsync(async db => await db.Database.ExecuteSqlAsync(
            $"""
             delete from "PackagePurchaseFeatures"
             where "PackagePurchaseId" in (
                 select "Id" from "PackagePurchases" where "PurchasedPackageId" = {packageId})
             """));

        var package = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/packages/{packageId}");

        Assert.Empty(package.GetProperty("features").EnumerateArray());

        // The catalogue entry still has its lines - they are simply not this package's to claim.
        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");
        Assert.NotEmpty(option.GetProperty("features").EnumerateArray());

        // The athlete's own route answers the same way rather than erroring.
        var athlete = await AthleteClientAsync(email);
        var mine = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/package");
        Assert.Empty(mine.GetProperty("features").EnumerateArray());
    }
}
