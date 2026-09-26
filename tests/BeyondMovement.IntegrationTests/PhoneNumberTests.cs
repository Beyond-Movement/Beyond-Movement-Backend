using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>Its own factory: these tests write the seeded Admin's phone, which others read as null.</summary>
public sealed class PhoneNumberApiFactory : ApiFactory;

/// <summary>
/// Phone numbers as account data for both roles: what is stored, what is refused, that a
/// profile save requires one while an account without one still works, that both roles' flows
/// share one rule, and that the field has not leaked into responses that never carried it. The parsing rules themselves are pinned in <c>PhonePolicyTests</c>; this is
/// the same behaviour proven end to end, down to the column.
/// </summary>
public sealed class PhoneNumberTests(PhoneNumberApiFactory factory) : IClassFixture<PhoneNumberApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private static int _counter;

    private async Task<(HttpClient Client, AuthPayload Auth)> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();

        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    private async Task<HttpClient> AdminAsync() =>
        (await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword)).Client;

    private async Task<(HttpClient Client, string Email)> AthleteAsync()
    {
        var email = $"phone{Interlocked.Increment(ref _counter)}@nowhere.test";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, "Phone Athlete", "Tennis", new DateOnly(2001, 4, 17));
        }

        return ((await SignInAsync(email, AthleteApiFactory.AthletePassword)).Client, email);
    }

    private static Task<HttpResponseMessage> SaveAdminAsync(HttpClient admin, string? phone) =>
        admin.PutAsJsonAsync("/api/v1/auth/me/profile", new { fullName = "Integration Admin", phone });

    private static Task<HttpResponseMessage> SaveAthleteAsync(HttpClient athlete, string? phone) =>
        athlete.PostAsJsonAsync("/api/v1/athletes/me/profile", new
        {
            fullName = "Phone Athlete",
            dateOfBirth = "2001-04-17",
            gender = "Female",
            sport = "Tennis",
            phone
        });

    /// <summary>What is in the column, bypassing every API shape — the thing this phase is about.</summary>
    private async Task<string?> StoredPhoneAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Phone).SingleAsync();
    }

    private static async Task<string?> ReturnedPhoneAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phone").GetString();

    // ---------------------------------------------------------- persistence

    [Theory]
    [InlineData("010 1234 5678", "+201012345678")]
    [InlineData("+44 20 7031 3000", "+442070313000")]
    [InlineData("+1 (650) 253-0000", "+16502530000")]
    public async Task An_admin_number_is_stored_and_returned_in_E164(string typed, string expected)
    {
        var admin = await AdminAsync();

        var response = await SaveAdminAsync(admin, typed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await ReturnedPhoneAsync(response));
        Assert.Equal(expected, await StoredPhoneAsync(ApiFactory.AdminEmail));
    }

    [Theory]
    [InlineData("0101-234-5678", "+201012345678")]
    [InlineData("0020 10 1234 5678", "+201012345678")]
    [InlineData("+971 50 123 4567", "+971501234567")]
    [InlineData("+33 6 12 34 56 78", "+33612345678")]
    public async Task An_athlete_number_is_stored_and_returned_in_E164(string typed, string expected)
    {
        var (athlete, email) = await AthleteAsync();

        var response = await SaveAthleteAsync(athlete, typed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await ReturnedPhoneAsync(response));
        Assert.Equal(expected, await StoredPhoneAsync(email));

        // And the Admin's view of the athlete reads the same stored value.
        Guid id;
        using (var scope = factory.Services.CreateScope())
            id = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        var admin = await AdminAsync();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes/{id}");
        Assert.Equal(expected, detail.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task The_same_number_typed_two_ways_by_two_roles_is_stored_identically()
    {
        var admin = await AdminAsync();
        var (athlete, email) = await AthleteAsync();

        (await SaveAdminAsync(admin, "010 1234 5678")).EnsureSuccessStatusCode();
        (await SaveAthleteAsync(athlete, "+20 (10) 1234-5678")).EnsureSuccessStatusCode();

        Assert.Equal(await StoredPhoneAsync(ApiFactory.AdminEmail), await StoredPhoneAsync(email));
    }

    /// <summary>
    /// Not unique: this is contact data, not a login, and a parent's number on two siblings'
    /// accounts is ordinary.
    /// </summary>
    [Fact]
    public async Task Two_accounts_may_share_a_number()
    {
        var (first, firstEmail) = await AthleteAsync();
        var (second, secondEmail) = await AthleteAsync();

        (await SaveAthleteAsync(first, "+20 122 345 6789")).EnsureSuccessStatusCode();
        (await SaveAthleteAsync(second, "+20 122 345 6789")).EnsureSuccessStatusCode();

        Assert.Equal("+201223456789", await StoredPhoneAsync(firstEmail));
        Assert.Equal("+201223456789", await StoredPhoneAsync(secondEmail));
    }

    // ------------------------------------------------------------- refusals

    /// <summary>
    /// Every one of these passes the old character-only rule — digits and allowed punctuation
    /// only — which is exactly why that rule was not enough.
    /// </summary>
    [Theory]
    [InlineData("12345")]
    [InlineData("010 1234")]
    [InlineData("+999 1234 5678")]
    [InlineData("0000000000")]
    [InlineData("+44 12")]
    public async Task A_well_punctuated_but_invalid_number_is_refused_for_both_roles(string invalid)
    {
        var admin = await AdminAsync();
        (await SaveAdminAsync(admin, "+20 100 123 4567")).EnsureSuccessStatusCode();

        var (athlete, email) = await AthleteAsync();
        (await SaveAthleteAsync(athlete, "+20 100 123 4567")).EnsureSuccessStatusCode();

        foreach (var response in new[] { await SaveAdminAsync(admin, invalid), await SaveAthleteAsync(athlete, invalid) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
            Assert.Single(problem.GetProperty("errors").GetProperty("Phone").EnumerateArray());
        }

        // A refused edit leaves the stored number alone.
        Assert.Equal("+201001234567", await StoredPhoneAsync(ApiFactory.AdminEmail));
        Assert.Equal("+201001234567", await StoredPhoneAsync(email));
    }

    // ---------------------------------------------------------- requiredness

    /// <summary>A marker for "leave the property out of the body entirely".</summary>
    private const string Missing = "<missing>";

    private static Task<HttpResponseMessage> SaveAdminWithoutPhoneAsync(HttpClient admin, string? phone) =>
        phone == Missing
            ? admin.PutAsJsonAsync("/api/v1/auth/me/profile", new { fullName = "Integration Admin" })
            : SaveAdminAsync(admin, phone);

    private static Task<HttpResponseMessage> SaveAthleteWithoutPhoneAsync(HttpClient athlete, string? phone) =>
        phone == Missing
            ? athlete.PostAsJsonAsync("/api/v1/athletes/me/profile", new
            {
                fullName = "Phone Athlete",
                dateOfBirth = "2001-04-17",
                gender = "Female",
                sport = "Tennis"
            })
            : SaveAthleteAsync(athlete, phone);

    private static async Task AssertPhoneRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());

        // Against Phone alone: every other field in the body is valid.
        var errors = problem.GetProperty("errors");
        Assert.Equal(["Phone"], errors.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Single(errors.GetProperty("Phone").EnumerateArray());
    }

    [Theory]
    [InlineData(Missing)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_admin_profile_save_without_a_phone_is_refused(string? phone)
    {
        var admin = await AdminAsync();
        (await SaveAdminAsync(admin, "+20 100 123 4567")).EnsureSuccessStatusCode();

        await AssertPhoneRequiredAsync(await SaveAdminWithoutPhoneAsync(admin, phone));

        // Refused, not cleared.
        Assert.Equal("+201001234567", await StoredPhoneAsync(ApiFactory.AdminEmail));
    }

    [Theory]
    [InlineData(Missing)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_athlete_profile_save_without_a_phone_is_refused(string? phone)
    {
        var (athlete, email) = await AthleteAsync();
        (await SaveAthleteAsync(athlete, "+20 100 123 4567")).EnsureSuccessStatusCode();

        await AssertPhoneRequiredAsync(await SaveAthleteWithoutPhoneAsync(athlete, phone));

        Assert.Equal("+201001234567", await StoredPhoneAsync(email));
    }

    /// <summary>
    /// Complete Profile is the first save, and it is refused too — the athlete stays where they
    /// were rather than reaching Home with a profile the backend calls incomplete.
    /// </summary>
    [Theory]
    [InlineData(Missing)]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Complete_profile_without_a_phone_leaves_the_athlete_incomplete(string? phone)
    {
        var email = $"phone{Interlocked.Increment(ref _counter)}@nowhere.test";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AthleteApiFactory.AddAthleteAsync(
                db, scope.ServiceProvider, email, fullName: null, sport: null, dateOfBirth: null);
        }

        var (athlete, _) = await SignInAsync(email, AthleteApiFactory.AthletePassword);

        await AssertPhoneRequiredAsync(await SaveAthleteWithoutPhoneAsync(athlete, phone));

        var me = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(me.GetProperty("profileCompleted").GetBoolean());
    }

    // --------------------------------------------------------------- schema

    [Fact]
    public async Task The_column_is_a_nullable_E164_width_varchar()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await db.Database.SqlQuery<ColumnInfo>($"""
            SELECT data_type AS "DataType", character_maximum_length AS "MaxLength", is_nullable AS "IsNullable"
            FROM information_schema.columns
            WHERE table_name = 'Users' AND column_name = 'Phone'
            """).SingleAsync();

        Assert.Equal("character varying", column.DataType);
        Assert.Equal(16, column.MaxLength);
        Assert.Equal("YES", column.IsNullable);
    }

    private sealed record ColumnInfo(string DataType, int MaxLength, string IsNullable);

    // ------------------------------------------------------------- exposure

    /// <summary>
    /// A phone on the user must not start riding along on responses that never carried it —
    /// above all the auth responses, which the app caches and which run on every start. Set a
    /// number first, so a leak would have a value to leak.
    /// </summary>
    [Fact]
    public async Task Phone_does_not_appear_in_auth_or_list_responses()
    {
        var admin = await AdminAsync();
        (await SaveAdminAsync(admin, "+20 100 123 4567")).EnsureSuccessStatusCode();

        var (athlete, athleteEmail) = await AthleteAsync();
        (await SaveAthleteAsync(athlete, "+20 111 222 3333")).EnsureSuccessStatusCode();

        var anonymous = factory.CreateClient();

        var bodies = new Dictionary<string, JsonElement>
        {
            ["admin login"] = await ReadAsync(anonymous.PostAsJsonAsync("/api/v1/auth/login",
                new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword })),
            ["athlete login"] = await ReadAsync(anonymous.PostAsJsonAsync("/api/v1/auth/login",
                new { email = athleteEmail, password = AthleteApiFactory.AthletePassword })),
            ["admin /auth/me"] = await ReadAsync(admin.GetAsync("/api/v1/auth/me")),
            ["athlete /auth/me"] = await ReadAsync(athlete.GetAsync("/api/v1/auth/me")),
            ["athlete list"] = await ReadAsync(admin.GetAsync("/api/v1/athletes"))
        };

        var (_, auth) = await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        bodies["refresh"] = await ReadAsync(anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = auth.RefreshToken }));

        foreach (var (name, body) in bodies)
            Assert.False(HasPropertyNamed(body, "phone"), $"{name} exposes a phone field.");
    }

    private static async Task<JsonElement> ReadAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static bool HasPropertyNamed(JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(p =>
            p.NameEquals(name) || HasPropertyNamed(p.Value, name)),
        JsonValueKind.Array => element.EnumerateArray().Any(e => HasPropertyNamed(e, name)),
        _ => false
    };

    // ------------------------------------------------------ existing accounts

    /// <summary>
    /// The seeded Admin is the account that exists before any of this: no phone. Nothing may
    /// refuse to sign it in or show it for that, and no number is invented for it - but the
    /// next save of its profile has to supply one.
    /// </summary>
    [Fact]
    public async Task An_admin_without_a_phone_signs_in_and_reads_but_must_supply_one_to_save()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Role == UserRole.Admin)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Phone, (string?)null));
        }

        var admin = await AdminAsync();

        (await admin.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
        var profile = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/profile");
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("phone").ValueKind);

        await AssertPhoneRequiredAsync(await SaveAdminAsync(admin, phone: null));
        Assert.Null(await StoredPhoneAsync(ApiFactory.AdminEmail));

        var saved = await SaveAdminAsync(admin, "010 1234 5678");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("+201012345678", await StoredPhoneAsync(ApiFactory.AdminEmail));
    }

    /// <summary>
    /// An athlete who completed their profile before phone numbers were required: still
    /// completed, still signs in, still reads their profile - and supplies a number on the
    /// next edit.
    /// </summary>
    [Fact]
    public async Task An_athlete_without_a_phone_signs_in_and_reads_but_must_supply_one_to_save()
    {
        var (athlete, email) = await AthleteAsync();
        Assert.Null(await StoredPhoneAsync(email));

        var me = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(me.GetProperty("profileCompleted").GetBoolean());

        var profile = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/athletes/me/profile");
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("phone").ValueKind);

        await AssertPhoneRequiredAsync(await SaveAthleteWithoutPhoneAsync(athlete, Missing));
        Assert.Null(await StoredPhoneAsync(email));

        var saved = await SaveAthleteAsync(athlete, "+44 7400 123456");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("+447400123456", await StoredPhoneAsync(email));
    }
}
