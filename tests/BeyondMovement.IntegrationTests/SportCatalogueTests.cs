using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BeyondMovement.IntegrationTests;

/// <summary>Its own fixture: one test writes a sport straight into the table.</summary>
public sealed class SportCatalogueApiFactory : ApiFactory;

/// <summary>
/// <c>GET /sports</c> — the catalogue the sport dropdown lists — and the table behind it.
/// </summary>
public sealed class SportCatalogueTests(SportCatalogueApiFactory factory)
    : IClassFixture<SportCatalogueApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken);

    private sealed record SportItem(Guid Id, string Name);

    /// <summary>
    /// The list the product approved, written out here rather than read from
    /// <see cref="SportCatalogue"/>, so a sport dropped or misspelled in the catalogue is caught
    /// rather than agreed with.
    /// </summary>
    private static readonly string[] Approved =
    [
        "Artistic Swimming", "Athletics", "Badminton", "Basketball", "Boxing", "Cycling", "Diving",
        "Equestrian", "Fencing", "Fin Swimming", "Football", "Golf", "Gymnastics", "Handball",
        "Judo", "Karate", "Modern Pentathlon", "Padel", "Rowing", "Rugby", "Sailing", "Shooting",
        "Squash", "Swimming", "Table Tennis", "Taekwondo", "Tennis", "Trampoline", "Triathlon",
        "Volleyball", "Water Polo", "Weightlifting", "Wrestling", "Other"
    ];

    private static int _counter;

    private async Task<HttpClient> SignedInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();

        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private Task<HttpClient> AdminAsync() => SignedInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteAsync()
    {
        var email = $"sports{Interlocked.Increment(ref _counter)}@nowhere.test";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, fullName: null, sport: null, dateOfBirth: null);
        }

        return await SignedInAsync(email, AthleteApiFactory.AthletePassword);
    }

    private static async Task<List<SportItem>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/sports");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SportItem>>(Json))!;
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task The_catalogue_requires_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/sports");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_read_the_catalogue()
    {
        Assert.Equal(Approved.Length, (await ListAsync(await AdminAsync())).Count);
    }

    /// <summary>
    /// Including one who has not finished Complete Profile — that screen is where the list is
    /// needed first.
    /// </summary>
    [Fact]
    public async Task An_athlete_can_read_the_catalogue_before_completing_their_profile()
    {
        Assert.Equal(Approved.Length, (await ListAsync(await AthleteAsync())).Count);
    }

    // ---------------------------------------------------------------- contents

    [Fact]
    public async Task The_catalogue_is_exactly_the_approved_sports()
    {
        var names = (await ListAsync(await AdminAsync())).Select(s => s.Name);

        Assert.Equal(Approved.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task No_two_sports_share_a_name_even_ignoring_case()
    {
        var sports = await ListAsync(await AdminAsync());

        Assert.Equal(sports.Count, sports.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(sports.Count, sports.Select(s => s.Id).Distinct().Count());
    }

    /// <summary>The ids are fixed in code, so a client or test can name a sport in any environment.</summary>
    [Fact]
    public async Task Every_sport_has_the_id_the_catalogue_fixes_for_it()
    {
        var sports = await ListAsync(await AdminAsync());

        foreach (var sport in sports)
            Assert.Equal(Sports.Id(sport.Name), sport.Id);

        Assert.Equal(SportCatalogue.Other, sports.Single(s => s.Name == "Other").Id);
    }

    [Fact]
    public async Task Each_entry_is_only_an_id_and_a_name()
    {
        var response = await (await AdminAsync()).GetAsync("/api/v1/sports");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(["id", "name"], body[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---------------------------------------------------------------- ordering

    [Fact]
    public async Task Sports_are_alphabetical_with_Other_last()
    {
        var names = (await ListAsync(await AdminAsync())).Select(s => s.Name).ToList();

        Assert.Equal("Other", names[^1]);

        // Alphabetical even though "Other" would otherwise fall between Modern Pentathlon and Padel.
        var ordinary = names[..^1];
        Assert.Equal(ordinary.Order(StringComparer.Ordinal), ordinary);
        Assert.Equal("Artistic Swimming", ordinary[0]);
        Assert.Equal("Wrestling", ordinary[^1]);
    }

    [Fact]
    public async Task The_order_is_the_same_on_every_call()
    {
        var admin = await AdminAsync();

        var first = (await ListAsync(admin)).Select(s => s.Id);
        var second = (await ListAsync(admin)).Select(s => s.Id);

        Assert.Equal(first, second);
    }

    // ------------------------------------------------------------------- table

    /// <summary>
    /// The unique index is on lower("Name"), which EF cannot express, so it exists only because
    /// the migration wrote it. Proven against the table rather than assumed.
    /// </summary>
    [Fact]
    public async Task The_table_refuses_a_name_that_differs_only_in_case()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""INSERT INTO "Sports" ("Id", "Name", "SortOrder") VALUES ({Guid.NewGuid()}, 'tennis', 0)"""));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
    }

    /// <summary>Somebody's profile points at it, so the foreign key refuses the delete.</summary>
    [Fact]
    public async Task A_sport_an_athlete_has_picked_cannot_be_deleted()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, $"sports.fk{Interlocked.Increment(ref _counter)}@nowhere.test",
                "Picked Rowing", "Rowing", new DateOnly(2000, 1, 1));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rowing = Sports.Id("Rowing");

            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync($"""DELETE FROM "Sports" WHERE "Id" = {rowing}"""));

            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        }
    }
}
