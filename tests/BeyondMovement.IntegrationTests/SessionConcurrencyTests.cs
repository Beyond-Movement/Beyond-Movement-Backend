using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeyondMovement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>One athlete per race, so a password changed or an account paused in one test cannot leak into another.</summary>
public sealed class SessionRaceApiFactory : ApiFactory
{
    public const string ChangeAthlete = "race-change@nowhere.test";
    public const string ResetAthlete = "race-reset@nowhere.test";
    public const string PauseAthlete = "race-pause@nowhere.test";
    public const string LogoutAthlete = "race-logout@nowhere.test";

    public Guid PauseAthleteId { get; private set; }

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var email in new[] { ChangeAthlete, ResetAthlete, LogoutAthlete })
            await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, email, "Race", "Tennis", new DateOnly(2000, 1, 1));

        PauseAthleteId = await AthleteApiFactory.AddAthleteAsync(
            db, scope.ServiceProvider, PauseAthlete, "Race", "Tennis", new DateOnly(2000, 1, 1));
    }
}

/// <summary>
/// Refresh raced against every operation that ends sessions. Each round releases the refresh and
/// the competing request at the same instant, many times over, and then checks the one thing that
/// matters: once the session-ending operation has completed, the user holds no refresh token that
/// still works - whichever request the database happened to serve first. And neither request may
/// fail with a 500, which is how a deadlock between the two would surface.
/// </summary>
public sealed class SessionConcurrencyTests(SessionRaceApiFactory factory) : IClassFixture<SessionRaceApiFactory>
{
    private const int Rounds = 25;

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Refresh_racing_a_password_change_leaves_no_usable_refresh_token()
    {
        var password = AthleteApiFactory.AthletePassword;

        for (var round = 0; round < Rounds; round++)
        {
            var next = round % 2 == 0 ? "Race#Changed-One2026" : "Race#Changed-Two2026";
            var auth = await LoginAsync(SessionRaceApiFactory.ChangeAthlete, password);
            var signedIn = Bearer(auth.AccessToken);

            var (refresh, change) = await RaceAsync(
                () => RefreshAsync(auth.RefreshToken),
                () => signedIn.PostAsJsonAsync("/api/v1/auth/change-password",
                    new { currentPassword = password, newPassword = next }));

            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            password = next;

            await AssertRefreshOutcomeAsync(refresh);
            await AssertNothingUsableAsync(SessionRaceApiFactory.ChangeAthlete, refresh);
        }
    }

    [Fact]
    public async Task Refresh_racing_a_password_reset_leaves_no_usable_refresh_token()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var auth = await LoginAsync(SessionRaceApiFactory.ResetAthlete, AthleteApiFactory.AthletePassword);
            var resetToken = await RequestResetTokenAsync(SessionRaceApiFactory.ResetAthlete);

            // The new password is the old one: the policy allows it, and the next round can log in.
            var (refresh, reset) = await RaceAsync(
                () => RefreshAsync(auth.RefreshToken),
                () => factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
                    new { token = resetToken, newPassword = AthleteApiFactory.AthletePassword }));

            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            await AssertRefreshOutcomeAsync(refresh);
            await AssertNothingUsableAsync(SessionRaceApiFactory.ResetAthlete, refresh);
        }
    }

    [Fact]
    public async Task Refresh_racing_a_pause_leaves_no_usable_refresh_token()
    {
        var admin = Bearer((await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword)).AccessToken);
        var athleteId = factory.PauseAthleteId;

        for (var round = 0; round < Rounds; round++)
        {
            var auth = await LoginAsync(SessionRaceApiFactory.PauseAthlete, AthleteApiFactory.AthletePassword);

            var (refresh, pause) = await RaceAsync(
                () => RefreshAsync(auth.RefreshToken),
                () => admin.PostAsync($"/api/v1/athletes/{athleteId}/pause", null));

            Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

            // A refresh served after the pause meets a token the pause revoked: 401. (403
            // ACCOUNT_PAUSED is for a token that is still live on a paused account.)
            await AssertRefreshOutcomeAsync(refresh, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
            await AssertNothingUsableAsync(SessionRaceApiFactory.PauseAthlete, refresh);

            Assert.Equal(HttpStatusCode.OK,
                (await admin.PostAsync($"/api/v1/athletes/{athleteId}/reactivate", null)).StatusCode);
        }
    }

    /// <summary>
    /// Logout ends the session the token belongs to. Raced against a refresh of that same token,
    /// the session must end either way - the refresh's replacement included.
    /// </summary>
    [Fact]
    public async Task Refresh_racing_a_logout_leaves_no_usable_token_in_that_session()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // A second session that must survive: logout ends one sign-in, not all of them.
            var other = await LoginAsync(SessionRaceApiFactory.LogoutAthlete, AthleteApiFactory.AthletePassword);
            var auth = await LoginAsync(SessionRaceApiFactory.LogoutAthlete, AthleteApiFactory.AthletePassword);
            var signedIn = Bearer(auth.AccessToken);

            var (refresh, logout) = await RaceAsync(
                () => RefreshAsync(auth.RefreshToken),
                () => signedIn.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = auth.RefreshToken }));

            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

            await AssertRefreshOutcomeAsync(refresh);
            if (refresh.StatusCode == HttpStatusCode.OK)
            {
                var replacement = (await refresh.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
                Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(replacement.RefreshToken)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(auth.RefreshToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(other.RefreshToken)).StatusCode);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Both requests start at the same instant, on different threads.</summary>
    private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(
        Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await gate.Task; return await first(); });
        var b = Task.Run(async () => { await gate.Task; return await second(); });

        gate.SetResult();
        return (await a, await b);
    }

    /// <summary>The refresh either won (200) or lost cleanly. Never a 500 - which is what a deadlock would be.</summary>
    private static async Task AssertRefreshOutcomeAsync(HttpResponseMessage refresh, params HttpStatusCode[] lostWith)
    {
        if (lostWith.Length == 0) lostWith = [HttpStatusCode.Unauthorized];

        Assert.True(refresh.StatusCode == HttpStatusCode.OK || lostWith.Contains(refresh.StatusCode),
            $"Refresh answered {(int)refresh.StatusCode}: {await refresh.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// No refresh token for the user is still active, and a replacement the refresh may have won
    /// is refused when presented.
    /// </summary>
    private async Task AssertNothingUsableAsync(string email, HttpResponseMessage refresh)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var survivors = await (from token in db.RefreshTokens
                                   join user in db.Users on token.UserId equals user.Id
                                   where user.Email == email
                                         && token.RevokedAtUtc == null && token.UsedAtUtc == null
                                         && token.ExpiresAtUtc > now
                                   select token.Id).CountAsync();
            Assert.Equal(0, survivors);
        }

        if (refresh.StatusCode == HttpStatusCode.OK)
        {
            var replacement = (await refresh.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
            var reuse = await RefreshAsync(replacement.RefreshToken);
            Assert.NotEqual(HttpStatusCode.OK, reuse.StatusCode);
        }
    }

    private async Task<AuthPayload> LoginAsync(string email, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.True(response.IsSuccessStatusCode, $"Login for {email} answered {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private HttpClient Bearer(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<string> RequestResetTokenAsync(string email)
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var mail = scope.ServiceProvider.GetRequiredService<TestEmailOutbox>().Messages.Last(m => m.To == email);
        return Uri.UnescapeDataString(Regex.Match(mail.TextBody, @"token=([^\s""&<]+)").Groups[1].Value);
    }
}
