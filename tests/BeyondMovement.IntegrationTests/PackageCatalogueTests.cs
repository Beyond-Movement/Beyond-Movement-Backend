using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 4: the package-option catalogue, loyalty, and per-athlete prices. Purchasing, payment
/// and remaining sessions are a later phase and are deliberately not exercised here.
/// </summary>
public sealed class PackageCatalogueTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
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

    /// <summary>Creates an option with a name unique to the calling test, and returns it.</summary>
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

    private static async Task<Guid> AthleteIdAsync(HttpClient admin, string email)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes?search={Uri.EscapeDataString(email)}");

        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    // ------------------------------------------------------------- catalogue

    [Fact]
    public async Task An_option_round_trips_with_its_features_in_the_order_they_were_sent()
    {
        var admin = await AdminClientAsync();

        var created = await CreateOptionAsync(admin, "Round Trip", features:
            ["Third", "First", "Second"]);

        var id = created.GetProperty("id").GetGuid();
        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{id}");

        // Not sorted alphabetically, not reordered by the database - exactly as submitted.
        Assert.Equal(
            ["Third", "First", "Second"],
            fetched.GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        Assert.Equal("EGP", fetched.GetProperty("currency").GetString());
        Assert.Equal(1, fetched.GetProperty("version").GetInt32());
        Assert.False(fetched.GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task A_duplicate_name_is_refused_whatever_its_casing()
    {
        var admin = await AdminClientAsync();
        await CreateOptionAsync(admin, "Duplicate Guard");

        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = "  duplicate GUARD  ",
            sessions = 4,
            defaultPriceMinor = 100_000,
            features = new[] { "One" }
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PACKAGE_NAME_CONFLICT", body.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(0, 400_000, new[] { "One" })]                 // sessions below the minimum
    [InlineData(1001, 400_000, new[] { "One" })]              // sessions above the maximum
    [InlineData(8, -1, new[] { "One" })]                      // negative price
    [InlineData(8, 400_000, new string[0])]                   // no features
    [InlineData(8, 400_000, new[] { "   " })]                 // a blank feature
    public async Task Invalid_input_is_refused_server_side(int sessions, long price, string[] features)
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = $"Invalid {sessions} {price} {features.Length}",
            sessions,
            defaultPriceMinor = price,
            features
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Eleven_features_is_too_many()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name = "Too Many Features",
            sessions = 8,
            defaultPriceMinor = 400_000,
            features = Enumerable.Range(1, 11).Select(i => $"Feature {i}").ToArray()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------- archive cycle

    [Fact]
    public async Task Archiving_hides_an_option_from_athletes_but_not_from_the_coach()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Archive Me");
        var id = created.GetProperty("id").GetGuid();

        var archived = await admin.PostAsJsonAsync(
            $"/api/v1/package-options/{id}/archive", new { version = 1 });
        archived.EnsureSuccessStatusCode();

        var active = await admin.GetFromJsonAsync<JsonElement>("/api/v1/package-options");
        var inArchive = await admin.GetFromJsonAsync<JsonElement>("/api/v1/package-options?archived=true");

        Assert.DoesNotContain(id, Ids(active));
        Assert.Contains(id, Ids(inArchive));

        // ...and the athlete's catalogue must not offer it at all.
        var athlete = await AthleteClientAsync("alex@nowhere.test");
        var catalogue = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");

        Assert.DoesNotContain(id, Ids(catalogue));
    }

    [Fact]
    public async Task An_archived_option_cannot_be_edited_until_it_is_restored()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Frozen While Archived");
        var id = created.GetProperty("id").GetGuid();

        var archived = await admin.PostAsJsonAsync(
            $"/api/v1/package-options/{id}/archive", new { version = 1 });
        var version = (await archived.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        var edit = await admin.PutAsJsonAsync($"/api/v1/package-options/{id}", new
        {
            name = "Frozen While Archived",
            sessions = 4,
            defaultPriceMinor = 200_000,
            features = new[] { "Changed" },
            version
        });

        var body = await edit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("PACKAGE_OPTION_ARCHIVED", body.GetProperty("errorCode").GetString());

        // Restore, and the same edit now goes through.
        var restored = await admin.PostAsJsonAsync(
            $"/api/v1/package-options/{id}/restore", new { version });
        restored.EnsureSuccessStatusCode();

        var restoredVersion = (await restored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        var retry = await admin.PutAsJsonAsync($"/api/v1/package-options/{id}", new
        {
            name = "Frozen While Archived",
            sessions = 4,
            defaultPriceMinor = 200_000,
            features = new[] { "Changed" },
            version = restoredVersion
        });

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task An_option_is_never_deleted_so_archiving_twice_is_refused()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Archive Twice");
        var id = created.GetProperty("id").GetGuid();

        var first = await admin.PostAsJsonAsync($"/api/v1/package-options/{id}/archive", new { version = 1 });
        var version = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();

        var second = await admin.PostAsJsonAsync($"/api/v1/package-options/{id}/archive", new { version });
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("PACKAGE_OPTION_ARCHIVED", body.GetProperty("errorCode").GetString());
    }

    // ----------------------------------------------------------- concurrency

    [Fact]
    public async Task A_stale_version_is_refused_rather_than_overwriting_the_other_device()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Two Devices");
        var id = created.GetProperty("id").GetGuid();

        // Both devices loaded version 1. The first saves.
        var firstSave = await admin.PutAsJsonAsync($"/api/v1/package-options/{id}", new
        {
            name = "Two Devices",
            sessions = 10,
            defaultPriceMinor = 500_000,
            features = new[] { "From the phone" },
            version = 1
        });
        firstSave.EnsureSuccessStatusCode();

        // The second still holds version 1, and must not silently win.
        var secondSave = await admin.PutAsJsonAsync($"/api/v1/package-options/{id}", new
        {
            name = "Two Devices",
            sessions = 6,
            defaultPriceMinor = 300_000,
            features = new[] { "From the tablet" },
            version = 1
        });

        var body = await secondSave.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, secondSave.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", body.GetProperty("errorCode").GetString());

        var current = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{id}");
        Assert.Equal(500_000, current.GetProperty("defaultPriceMinor").GetInt64());
    }

    // ------------------------------------------------------------ scoping

    [Fact]
    public async Task Another_coachs_option_is_not_found_rather_than_forbidden()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/package-options/{Guid.NewGuid()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PACKAGE_OPTION_NOT_FOUND", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_reach_the_admin_catalogue_endpoints()
    {
        var athlete = await AthleteClientAsync("alex@nowhere.test");

        var response = await athlete.GetAsync("/api/v1/package-options");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------- loyalty and overrides

    [Fact]
    public async Task Loyalty_takes_fifteen_percent_off_every_option_for_that_athlete_only()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Loyalty Priced", priceMinor: 400_000);
        var id = created.GetProperty("id").GetGuid();

        var loyalId = await AthleteIdAsync(admin, "jordan@nowhere.test");
        var loyalty = await admin.PutAsJsonAsync($"/api/v1/athletes/{loyalId}/loyalty", new { isLoyal = true });
        loyalty.EnsureSuccessStatusCode();

        var loyal = await AthleteClientAsync("jordan@nowhere.test");
        var ordinary = await AthleteClientAsync("alex@nowhere.test");

        Assert.Equal(340_000, await PriceOfAsync(loyal, id));
        Assert.Equal(400_000, await PriceOfAsync(ordinary, id));

        // ...and the flag surfaces on the Admin screens.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes/{loyalId}");
        Assert.True(detail.GetProperty("isLoyal").GetBoolean());

        // Put it back, so the fixture is not left changed for another test.
        await admin.PutAsJsonAsync($"/api/v1/athletes/{loyalId}/loyalty", new { isLoyal = false });
    }

    [Fact]
    public async Task A_custom_price_beats_loyalty_and_removing_it_restores_the_calculation()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "Override Priced", priceMinor: 400_000);
        var id = created.GetProperty("id").GetGuid();

        var athleteId = await AthleteIdAsync(admin, "sam@nowhere.test");
        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = true });

        var set = await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{id}", new { priceMinor = 250_000 });
        set.EnsureSuccessStatusCode();

        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes/{athleteId}/catalogue");
        Assert.Equal(250_000, PriceIn(preview, id));

        // Setting it again moves the price rather than adding a second override.
        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{id}", new { priceMinor = 260_000 });

        var overrides = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteId}/custom-prices");
        Assert.Single(overrides.EnumerateArray(), o => o.GetProperty("packageOptionId").GetGuid() == id);

        var removed = await admin.DeleteAsync($"/api/v1/athletes/{athleteId}/custom-prices/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // With the override gone, loyalty applies again - not the default price.
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes/{athleteId}/catalogue");
        Assert.Equal(340_000, PriceIn(after, id));

        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = false });
    }

    [Fact]
    public async Task Removing_an_override_that_does_not_exist_is_a_not_found()
    {
        var admin = await AdminClientAsync();
        var created = await CreateOptionAsync(admin, "No Override Here");
        var id = created.GetProperty("id").GetGuid();
        var athleteId = await AthleteIdAsync(admin, "alex@nowhere.test");

        var response = await admin.DeleteAsync($"/api/v1/athletes/{athleteId}/custom-prices/{id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("CUSTOM_PRICE_NOT_FOUND", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_athlete_catalogue_shows_a_price_and_never_how_it_was_reached()
    {
        var admin = await AdminClientAsync();
        await CreateOptionAsync(admin, "Opaque Pricing");

        var athlete = await AthleteClientAsync("alex@nowhere.test");
        var catalogue = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");

        var item = catalogue.EnumerateArray().First();
        var fields = item.EnumerateObject().Select(p => p.Name).ToArray();

        // A "was 4000" the athlete never agreed to would be an invention, and telling them a
        // discount applied invites "why not me?" between athletes.
        Assert.DoesNotContain("defaultPriceMinor", fields);
        Assert.DoesNotContain("isLoyal", fields);
        Assert.DoesNotContain("discount", fields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("priceMinor", fields);
        Assert.Equal("EGP", item.GetProperty("currency").GetString());
    }

    private static async Task<long> PriceOfAsync(HttpClient athlete, Guid optionId)
    {
        var catalogue = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");
        return PriceIn(catalogue, optionId);
    }

    private static long PriceIn(JsonElement catalogue, Guid optionId) =>
        catalogue.EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == optionId)
            .GetProperty("priceMinor")
            .GetInt64();

    private static IEnumerable<Guid> Ids(JsonElement list)
    {
        var items = list.ValueKind == JsonValueKind.Array ? list : list.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
    }
}
