using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 2: the Admin's athlete list, profile, and pause/reactivate.
/// <para>
/// Athletes are seeded straight into the database rather than driven through the invitation
/// flow — these tests are about listing and status, and a six-step onboarding per athlete
/// would make them slow and hide what is being asserted.
/// </para>
/// </summary>
public sealed class AthleteManagementTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
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

    private async Task<JsonElement> ListAsync(HttpClient admin, string query = "")
    {
        var response = await admin.GetAsync("/api/v1/athletes" + query);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Null entries are real: an athlete who has not completed their profile has no name, and
    /// the list still shows them. Keeping the null here rather than substituting a placeholder
    /// is what lets the ordering assertions say where those rows belong.
    /// </summary>
    private static string?[] Names(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString())];

    /// <summary>
    /// Compares as a sequence, so a null name is an ordinary element rather than something the
    /// assertion overloads refuse to take.
    /// </summary>
    private static void AssertNames(JsonElement page, params string?[] expected) =>
        Assert.Equal<IEnumerable<string?>>(expected, Names(page));

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task The_list_returns_the_coachs_athletes_with_paging_metadata()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin);

        Assert.Equal(AthleteApiFactory.SeededAthletes, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.True(page.GetProperty("pageSize").GetInt32() > 0);
        Assert.True(page.GetProperty("totalPages").GetInt32() >= 1);
    }

    [Fact]
    public async Task Search_is_case_insensitive_trimmed_and_matches_part_of_a_name()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, "?search=%20%20oRDaN%20%20");   // "  oRDaN  " inside "Jordan"

        AssertNames(page, "Jordan Blake");
    }

    [Fact]
    public async Task Search_also_matches_the_sport()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, "?search=tennis");

        AssertNames(page, "Alex Thompson");
    }

    [Fact]
    public async Task Search_also_matches_the_email()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, "?search=jordan@nowhere");

        AssertNames(page, "Jordan Blake");
    }

    [Fact]
    public async Task An_athlete_with_no_name_yet_is_still_listed_and_findable_by_email()
    {
        var admin = await AdminClientAsync();

        // This athlete registered with a password and stopped, so there is no name to search
        // for at all. Without the email fallback the coach could not find their own invitee.
        var page = await ListAsync(admin, "?search=nameless@nowhere");
        var items = page.GetProperty("items").EnumerateArray().ToList();

        // A null name is the list's own signal that the athlete has not finished: the row
        // carries no profileCompleted flag, so the app shows the email in its place.
        var athlete = Assert.Single(items);
        Assert.Equal(JsonValueKind.Null, athlete.GetProperty("fullName").ValueKind);
        Assert.Equal("nameless@nowhere.test", athlete.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Every_row_carries_both_the_user_id_and_the_profile_id()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, "?search=alex@nowhere");
        var athlete = Assert.Single(page.GetProperty("items").EnumerateArray().ToList());

        var userId = athlete.GetProperty("id").GetGuid();
        var profileId = athlete.GetProperty("athleteProfileId").GetGuid();

        Assert.NotEqual(Guid.Empty, profileId);

        // The two are different ids for different things, and the whole reason the row carries
        // both: /athletes/{athleteId} takes the user id, while sessions and packages are keyed
        // by the profile id. Asserting they differ is what would catch the wrong one being
        // mapped — a bug that would otherwise only surface as a 404 in the mobile app.
        Assert.NotEqual(userId, profileId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var expected = await db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.Id)
            .SingleAsync();

        Assert.Equal(expected, profileId);
    }

    [Fact]
    public async Task Every_row_carries_an_email_even_when_it_has_no_name()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin);

        // The row shows the email whenever the name is null, so an absent email would leave the
        // coach looking at a blank line - and a client that models it as required cannot parse
        // the response at all.
        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.TryGetProperty("email", out var email), "every list row needs an email");
            Assert.False(string.IsNullOrWhiteSpace(email.GetString()));
        }
    }

    [Fact]
    public async Task A_search_matching_nothing_returns_an_empty_page_not_an_error()
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, "?search=nobodyhasthisname");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
        Assert.False(page.GetProperty("hasNextPage").GetBoolean());
    }

    [Fact]
    public async Task Wildcard_characters_in_a_search_are_matched_literally()
    {
        var admin = await AdminClientAsync();

        // Unescaped, "%" would match every athlete instead of none.
        var page = await ListAsync(admin, "?search=%25");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("NameAsc", "Alex Thompson")]
    [InlineData("NameDesc", "Sam Reed")]
    [InlineData("NewestFirst", "Robin Vale")]
    [InlineData("OldestFirst", "Alex Thompson")]
    public async Task Sorting_orders_the_list(string sort, string expectedFirst)
    {
        var admin = await AdminClientAsync();

        var page = await ListAsync(admin, $"?sort={sort}");

        Assert.Equal(expectedFirst, Names(page)[0]);
    }

    [Fact]
    public async Task Sorting_by_sport_puts_athletes_without_one_last()
    {
        var admin = await AdminClientAsync();

        var names = Names(await ListAsync(admin, "?sort=Sport"));

        // Otherwise a blank sport would head the list and look like a bug to the coach. Two
        // athletes have no sport — one named, one not — and both belong at the end.
        Assert.Equal<IEnumerable<string?>>(["Robin Vale", null], names[^2..]);
    }

    [Fact]
    public async Task The_status_filter_selects_by_account_status()
    {
        var admin = await AdminClientAsync();

        var paused = await ListAsync(admin, "?status=Paused");
        var active = await ListAsync(admin, "?status=Active");

        AssertNames(paused, "Sam Reed");
        Assert.DoesNotContain("Sam Reed", Names(active));
    }

    [Fact]
    public async Task Paused_athletes_stay_visible_to_their_coach()
    {
        var admin = await AdminClientAsync();

        // Pausing removes the athlete's access, not the coach's view of them.
        Assert.Contains("Sam Reed", Names(await ListAsync(admin)));
    }

    [Fact]
    public async Task Page_size_is_clamped_rather_than_obeyed()
    {
        var admin = await AdminClientAsync();

        var huge = await ListAsync(admin, "?pageSize=5000");
        var zero = await ListAsync(admin, "?pageSize=0&page=0");

        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, zero.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, zero.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Paging_does_not_repeat_or_drop_rows()
    {
        var admin = await AdminClientAsync();

        var first = Names(await ListAsync(admin, "?pageSize=2&page=1"));
        var second = Names(await ListAsync(admin, "?pageSize=2&page=2"));

        Assert.Equal(2, first.Length);
        Assert.Empty(first.Intersect(second));
    }

    // ---------------------------------------------------------------- detail

    [Fact]
    public async Task The_profile_returns_the_fields_the_admin_screen_needs()
    {
        var admin = await AdminClientAsync();
        var id = (await ListAsync(admin, "?search=Alex")).GetProperty("items")[0].GetProperty("id").GetGuid();

        var athlete = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes/{id}");

        Assert.Equal("Alex Thompson", athlete.GetProperty("fullName").GetString());
        Assert.Equal("Tennis", athlete.GetProperty("sport").GetString());
        Assert.Equal("Female", athlete.GetProperty("gender").GetString());
        Assert.Equal("2001-04-17", athlete.GetProperty("dateOfBirth").GetString());
        Assert.Equal("Active", athlete.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, athlete.GetProperty("phone").ValueKind);   // nothing collects it yet
    }

    [Fact]
    public async Task An_unknown_athlete_is_not_found()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/athletes/{Guid.NewGuid()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Another_coachs_athlete_is_not_found_rather_than_forbidden()
    {
        var admin = await AdminClientAsync();

        // A 403 would confirm the record exists, which is itself a disclosure.
        var response = await admin.GetAsync($"/api/v1/athletes/{AthleteApiFactory.ForeignAthleteId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ATHLETE_NOT_FOUND", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_foreign_athlete_never_appears_in_the_list()
    {
        var admin = await AdminClientAsync();

        Assert.DoesNotContain("Foreign Athlete", Names(await ListAsync(admin, "?pageSize=100")));
    }
}
