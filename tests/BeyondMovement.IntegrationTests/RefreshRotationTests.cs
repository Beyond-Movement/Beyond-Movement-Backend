using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The app with a clock the tests drive, so "inside" and "outside" the superseded-refresh grace
/// window are exact instants rather than sleeps. Its own athletes, because a password reset here
/// must not sign out the shared Admin other suites log in as.
/// </summary>
public sealed class RefreshRotationApiFactory : ApiFactory
{
    public const string Athlete = "rotation@nowhere.test";
    public const string ResetAthlete = "rotation-reset@nowhere.test";
    public const string ChangeAthlete = "rotation-change@nowhere.test";

    public FixedClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, Athlete, "Rotation", "Tennis", new DateOnly(2000, 1, 1));
        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, ResetAthlete, "Rotation Reset", "Tennis", new DateOnly(2000, 1, 1));
        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, ChangeAthlete, "Rotation Change", "Tennis", new DateOnly(2000, 1, 1));
    }
}

/// <summary>
/// Refresh rotation against the real database: a token is spent successfully exactly once, a
/// duplicate moments later is told it was superseded without forking or revoking the family, and
/// a later replay still revokes the family as theft.
/// </summary>
public sealed class RefreshRotationTests : IClassFixture<RefreshRotationApiFactory>
{
    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RefreshRotationApiFactory _factory;

    public RefreshRotationTests(RefreshRotationApiFactory factory)
    {
        _factory = factory;

        // Every test starts at real time; tests in a class run one at a time. On a whole
        // millisecond, because PostgreSQL keeps microseconds and drops the rest: from an instant
        // with sub-microsecond ticks, "exactly ten seconds after the stored UsedAtUtc" would be
        // a fraction of a microsecond past the window.
        var now = DateTime.UtcNow;
        _factory.Clock.UtcNow = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    // ------------------------------------------------------------ normal rotation

    [Fact]
    public async Task A_valid_token_rotates_into_exactly_one_replacement()
    {
        var login = await LoginAsync();

        var response = await RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.False(string.IsNullOrEmpty(rotated.AccessToken));

        var family = await FamilyOfAsync(login.RefreshToken);
        Assert.Equal(2, family.Count);
        Assert.NotNull(family.Single(t => t.TokenHash == Hash(login.RefreshToken)).UsedAtUtc);
        Assert.Single(family, t => t.IsActive(_factory.Clock.UtcNow));
        Assert.All(family, t => Assert.Null(t.RevokedAtUtc));
    }

    // -------------------------------------------------------------- concurrency

    /// <summary>
    /// Many requests released together with the same token, as several tabs or a retried call
    /// would send them. Before rotation was atomic, more than one could read the token as unused
    /// and each issue its own replacement - a forked family.
    /// </summary>
    [Fact]
    public async Task Concurrent_refreshes_with_one_token_produce_one_replacement_and_no_fork()
    {
        const int Requests = 10;
        var login = await LoginAsync();
        var client = _factory.CreateClient();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, Requests).Select(async _ =>
        {
            await gate.Task;
            return await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken });
        }).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToArray();
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToArray();

        Assert.Single(winners);
        Assert.Equal(Requests - 1, losers.Length);
        foreach (var loser in losers)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, loser.StatusCode);
            Assert.Equal("REFRESH_SUPERSEDED", await ErrorCodeAsync(loser));
        }

        // One replacement, nothing revoked by the losers.
        var family = await FamilyOfAsync(login.RefreshToken);
        Assert.Equal(2, family.Count);
        Assert.All(family, t => Assert.Null(t.RevokedAtUtc));

        // And the winner's replacement still works.
        var winner = (await winners[0].Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(winner.RefreshToken)).StatusCode);
    }

    // ------------------------------------------------------- inside the grace window

    [Fact]
    public async Task An_immediate_duplicate_is_superseded_and_neither_issues_nor_revokes()
    {
        var login = await LoginAsync();
        var rotated = await RefreshOkAsync(login.RefreshToken);

        _factory.Clock.UtcNow += TimeSpan.FromSeconds(3);
        var duplicate = await RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        Assert.Equal("REFRESH_SUPERSEDED", await ErrorCodeAsync(duplicate));

        var family = await FamilyOfAsync(login.RefreshToken);
        Assert.Equal(2, family.Count);
        Assert.All(family, t => Assert.Null(t.RevokedAtUtc));

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(rotated.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task The_grace_window_ends_at_exactly_ten_seconds()
    {
        var login = await LoginAsync();
        await RefreshOkAsync(login.RefreshToken);

        _factory.Clock.UtcNow += RefreshToken.SupersededGrace;
        Assert.Equal("REFRESH_SUPERSEDED", await ErrorCodeAsync(await RefreshAsync(login.RefreshToken)));

        _factory.Clock.UtcNow += TimeSpan.FromMilliseconds(1);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(await RefreshAsync(login.RefreshToken)));
    }

    // ------------------------------------------------------ outside the grace window

    /// <summary>Theft detection, unchanged: a spent token presented later kills the family.</summary>
    [Fact]
    public async Task Reusing_a_spent_token_after_the_grace_window_revokes_the_whole_family()
    {
        var login = await LoginAsync();
        var rotated = await RefreshOkAsync(login.RefreshToken);

        _factory.Clock.UtcNow += RefreshToken.SupersededGrace + TimeSpan.FromSeconds(1);
        var replay = await RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(replay));

        var family = await FamilyOfAsync(login.RefreshToken);
        Assert.All(family, t => Assert.NotNull(t.RevokedAtUtc));

        // The legitimate holder's newest token dies too - that is the point of family revocation.
        var afterRevocation = await RefreshAsync(rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(afterRevocation));
    }

    /// <summary>
    /// A duplicate is only innocent while its token is unrevoked. A password change revokes every
    /// token the user holds, spent ones included, so a duplicate seconds later is plainly invalid.
    /// </summary>
    [Fact]
    public async Task A_duplicate_inside_the_window_after_a_password_change_is_simply_invalid()
    {
        var login = await LoginAsync(RefreshRotationApiFactory.ChangeAthlete);
        var rotated = await RefreshOkAsync(login.RefreshToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = AthleteApiFactory.AthletePassword, newPassword = "Changed#Password2026" })).StatusCode);

        var duplicate = await RefreshAsync(login.RefreshToken);

        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(duplicate));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(await RefreshAsync(rotated.RefreshToken)));
    }

    // -------------------------------------------------------- existing behaviour

    [Fact]
    public async Task An_unknown_token_is_invalid()
    {
        var response = await RefreshAsync(Convert.ToBase64String(new byte[32]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_expired_token_is_invalid_and_is_not_spent()
    {
        var login = await LoginAsync();

        _factory.Clock.UtcNow += TimeSpan.FromDays(31);
        var response = await RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(response));

        var family = await FamilyOfAsync(login.RefreshToken);
        Assert.Single(family);
        Assert.Null(family[0].UsedAtUtc);
    }

    [Fact]
    public async Task A_token_revoked_by_logout_is_invalid()
    {
        var login = await LoginAsync();
        await LogoutAllAsync(login);

        var response = await RefreshAsync(login.RefreshToken);

        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(response));
        Assert.Single(await FamilyOfAsync(login.RefreshToken));
    }

    /// <summary>
    /// The successful reset, end to end: the emailed link's token sets a new password and every
    /// refresh token the user held dies - including one rotated moments before.
    /// </summary>
    [Fact]
    public async Task A_completed_password_reset_revokes_every_refresh_token()
    {
        var first = await LoginAsync(RefreshRotationApiFactory.ResetAthlete);
        var second = await LoginAsync(RefreshRotationApiFactory.ResetAthlete);
        var rotated = await RefreshOkAsync(second.RefreshToken);

        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new { email = RefreshRotationApiFactory.ResetAthlete })).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var mail = scope.ServiceProvider.GetRequiredService<TestEmailOutbox>().Messages
                .Last(m => m.To == RefreshRotationApiFactory.ResetAthlete);
            var token = Uri.UnescapeDataString(Regex.Match(mail.TextBody, @"token=([^\s""&<]+)").Groups[1].Value);

            var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
                new { token, newPassword = "Brand#New-Password2026" });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(await RefreshAsync(first.RefreshToken)));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(await RefreshAsync(rotated.RefreshToken)));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<AuthPayload> LoginAsync(string email = RefreshRotationApiFactory.Athlete)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = AthleteApiFactory.AthletePassword });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private async Task<AuthPayload> RefreshOkAsync(string refreshToken)
    {
        var response = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
    }

    private async Task LogoutAllAsync(AuthPayload auth)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = auth.RefreshToken })).StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private string Hash(string raw) => _factory.Services.GetRequiredService<ITokenService>().Hash(raw);

    /// <summary>Every token sharing a family with this one, read fresh from the database.</summary>
    private async Task<List<RefreshToken>> FamilyOfAsync(string rawToken)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = Hash(rawToken);

        var familyId = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => t.FamilyId).SingleAsync();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.FamilyId == familyId).ToListAsync();
    }
}
