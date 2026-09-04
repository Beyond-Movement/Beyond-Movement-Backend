using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests set loyalty and price overrides on athletes, which the catalogue
/// suite asserts against.
/// </summary>
public sealed class AthletePricingApiFactory : ApiFactory
{
    private int _counter;

    /// <summary>An athlete nobody else is pricing, so overrides here cannot disturb another test.</summary>
    public async Task<(Guid UserId, string Email)> NewAthleteAsync()
    {
        var ordinal = Interlocked.Increment(ref _counter);
        var email = $"priced{ordinal}@nowhere.test";

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.AppDbContext>();

        var userId = await AthleteApiFactory.AddAthleteAsync(
            db, scope.ServiceProvider, email, $"Priced {ordinal}", "Tennis", new DateOnly(2000, 1, 1));

        return (userId, email);
    }
}

/// <summary>
/// The Admin pricing view — one call that answers "what does this athlete pay, and why?".
/// <para>
/// It exists so the app never reproduces the precedence rule. These tests are therefore mostly
/// about the <em>source</em> label: the price alone was already covered by the catalogue suite,
/// and a price without a correct reason beside it is what this endpoint was built to prevent.
/// </para>
/// </summary>
public sealed class AthletePricingTests(AthletePricingApiFactory factory)
    : IClassFixture<AthletePricingApiFactory>
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

    private static async Task<Guid> CreateOptionAsync(
        HttpClient admin, string name, long priceMinor = 400_000, int sessions = 8)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/package-options", new
        {
            name, sessions, defaultPriceMinor = priceMinor, features = new[] { "Weekly video call" }
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PricingAsync(HttpClient admin, Guid athleteId)
    {
        var response = await admin.GetAsync($"/api/v1/athletes/{athleteId}/pricing");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement ItemFor(JsonElement pricing, Guid optionId) =>
        pricing.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("packageOptionId").GetGuid() == optionId);

    // --- the three sources ---------------------------------------------------

    [Fact]
    public async Task A_plain_athlete_pays_the_default_and_the_source_says_so()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Default", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        var pricing = await PricingAsync(admin, athleteId);
        var item = ItemFor(pricing, optionId);

        Assert.False(pricing.GetProperty("isLoyal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pricing.GetProperty("loyaltyDiscountPercent").ValueKind);
        Assert.Equal(400_000, item.GetProperty("defaultPriceMinor").GetInt64());
        Assert.Equal(400_000, item.GetProperty("effectivePriceMinor").GetInt64());
        Assert.Equal("Default", item.GetProperty("pricingSource").GetString());
    }

    [Fact]
    public async Task A_loyal_athlete_pays_the_discount_and_the_percentage_is_stated()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Loyalty", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = true });

        var pricing = await PricingAsync(admin, athleteId);
        var item = ItemFor(pricing, optionId);

        Assert.True(pricing.GetProperty("isLoyal").GetBoolean());
        Assert.Equal(15, pricing.GetProperty("loyaltyDiscountPercent").GetInt32());

        // The default is still reported, because the screen shows what the discount is off.
        Assert.Equal(400_000, item.GetProperty("defaultPriceMinor").GetInt64());
        Assert.Equal(340_000, item.GetProperty("effectivePriceMinor").GetInt64());
        Assert.Equal("Loyalty", item.GetProperty("pricingSource").GetString());
    }

    [Fact]
    public async Task An_override_reports_custom_and_the_default_it_replaced()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Custom", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{optionId}", new { priceMinor = 250_000 });

        var item = ItemFor(await PricingAsync(admin, athleteId), optionId);

        Assert.Equal(400_000, item.GetProperty("defaultPriceMinor").GetInt64());
        Assert.Equal(250_000, item.GetProperty("effectivePriceMinor").GetInt64());
        Assert.Equal("Custom", item.GetProperty("pricingSource").GetString());
    }

    /// <summary>
    /// The precedence a client would most likely get wrong, asserted end to end rather than only
    /// on the domain: an override is an agreed price, not a starting point.
    /// </summary>
    [Fact]
    public async Task An_override_beats_loyalty_and_is_not_discounted_again()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Both", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = true });
        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{optionId}", new { priceMinor = 250_000 });

        var pricing = await PricingAsync(admin, athleteId);
        var item = ItemFor(pricing, optionId);

        Assert.True(pricing.GetProperty("isLoyal").GetBoolean());
        Assert.Equal(250_000, item.GetProperty("effectivePriceMinor").GetInt64());   // not 212,500
        Assert.Equal("Custom", item.GetProperty("pricingSource").GetString());
    }

    /// <summary>
    /// Loyalty is athlete-level and the override is per option, so one loyal athlete can show
    /// both sources at once. The percentage in the envelope describes only the Loyalty rows.
    /// </summary>
    [Fact]
    public async Task One_athlete_can_show_different_sources_on_different_options()
    {
        var admin = await AdminClientAsync();
        var overridden = await CreateOptionAsync(admin, "Pricing Mixed A", priceMinor: 400_000);
        var plain = await CreateOptionAsync(admin, "Pricing Mixed B", priceMinor: 200_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = true });
        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{overridden}", new { priceMinor = 250_000 });

        var pricing = await PricingAsync(admin, athleteId);

        Assert.Equal("Custom", ItemFor(pricing, overridden).GetProperty("pricingSource").GetString());
        Assert.Equal(250_000, ItemFor(pricing, overridden).GetProperty("effectivePriceMinor").GetInt64());

        Assert.Equal("Loyalty", ItemFor(pricing, plain).GetProperty("pricingSource").GetString());
        Assert.Equal(170_000, ItemFor(pricing, plain).GetProperty("effectivePriceMinor").GetInt64());
    }

    [Fact]
    public async Task Removing_an_override_returns_the_row_to_loyalty_or_default()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Removed", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{optionId}", new { priceMinor = 250_000 });
        Assert.Equal("Custom",
            ItemFor(await PricingAsync(admin, athleteId), optionId).GetProperty("pricingSource").GetString());

        await admin.DeleteAsync($"/api/v1/athletes/{athleteId}/custom-prices/{optionId}");

        var item = ItemFor(await PricingAsync(admin, athleteId), optionId);
        Assert.Equal("Default", item.GetProperty("pricingSource").GetString());
        Assert.Equal(400_000, item.GetProperty("effectivePriceMinor").GetInt64());
    }

    /// <summary>
    /// The source is what tells the screen whether Remove Custom Price applies, so a number that
    /// happens to equal the default must still read as Custom.
    /// </summary>
    [Fact]
    public async Task An_override_matching_the_default_still_reports_custom()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Same", priceMinor: 400_000);
        var (athleteId, _) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync(
            $"/api/v1/athletes/{athleteId}/custom-prices/{optionId}", new { priceMinor = 400_000 });

        var item = ItemFor(await PricingAsync(admin, athleteId), optionId);

        Assert.Equal(400_000, item.GetProperty("effectivePriceMinor").GetInt64());
        Assert.Equal("Custom", item.GetProperty("pricingSource").GetString());
    }

    // --- shape and scope -----------------------------------------------------

    [Fact]
    public async Task The_response_carries_the_athlete_standing_and_a_row_per_option()
    {
        var admin = await AdminClientAsync();
        await CreateOptionAsync(admin, "Pricing Shape");
        var (athleteId, _) = await factory.NewAthleteAsync();

        var pricing = await PricingAsync(admin, athleteId);

        Assert.Equal(
            ["athleteUserId", "isLoyal", "loyaltyDiscountPercent", "currency", "items"],
            pricing.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(athleteId, pricing.GetProperty("athleteUserId").GetGuid());
        Assert.Equal("EGP", pricing.GetProperty("currency").GetString());

        Assert.Equal(
            ["packageOptionId", "name", "sessions", "defaultPriceMinor", "effectivePriceMinor", "pricingSource"],
            pricing.GetProperty("items").EnumerateArray().First()
                .EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Archived_options_are_left_out_because_nobody_can_buy_them()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Archived");
        var (athleteId, _) = await factory.NewAthleteAsync();

        Assert.Contains(
            (await PricingAsync(admin, athleteId)).GetProperty("items").EnumerateArray(),
            x => x.GetProperty("packageOptionId").GetGuid() == optionId);

        var option = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/package-options/{optionId}");
        await admin.PostAsJsonAsync($"/api/v1/package-options/{optionId}/archive",
            new { version = option.GetProperty("version").GetInt32() });

        Assert.DoesNotContain(
            (await PricingAsync(admin, athleteId)).GetProperty("items").EnumerateArray(),
            x => x.GetProperty("packageOptionId").GetGuid() == optionId);
    }

    [Fact]
    public async Task Rows_are_ordered_by_name_to_match_the_package_options_screen()
    {
        var admin = await AdminClientAsync();
        var (athleteId, _) = await factory.NewAthleteAsync();

        var names = (await PricingAsync(admin, athleteId)).GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal([.. names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)], names);
    }

    [Fact]
    public async Task An_unknown_athlete_is_404_rather_than_an_empty_list()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/athletes/{Guid.NewGuid()}/pricing");

        // An empty items list is a real answer - a coach with no options - so a bad id must not
        // be able to look like one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_athlete_cannot_read_the_pricing_policy_behind_their_own_prices()
    {
        var (athleteId, email) = await factory.NewAthleteAsync();
        var athlete = await SignInAsync(email, AthleteApiFactory.AthletePassword);

        // The whole point of a separate Admin shape: an athlete sees a price, never the policy.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync($"/api/v1/athletes/{athleteId}/pricing")).StatusCode);
    }

    /// <summary>
    /// The two views must agree to the piastre, or the coach quotes one number and the athlete
    /// is charged another.
    /// </summary>
    [Fact]
    public async Task The_effective_price_is_the_number_the_athlete_actually_sees()
    {
        var admin = await AdminClientAsync();
        var optionId = await CreateOptionAsync(admin, "Pricing Agrees", priceMinor: 99_999);
        var (athleteId, email) = await factory.NewAthleteAsync();

        await admin.PutAsJsonAsync($"/api/v1/athletes/{athleteId}/loyalty", new { isLoyal = true });

        var adminPrice = ItemFor(await PricingAsync(admin, athleteId), optionId)
            .GetProperty("effectivePriceMinor").GetInt64();

        var athlete = await SignInAsync(email, AthleteApiFactory.AthletePassword);
        var catalogue = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");

        var athletePrice = catalogue.EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == optionId)
            .GetProperty("priceMinor").GetInt64();

        Assert.Equal(athletePrice, adminPrice);
    }
}
