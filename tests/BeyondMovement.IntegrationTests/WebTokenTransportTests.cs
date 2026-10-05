using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeyondMovement.Api.Authentication;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The PWA's arrangement: one trusted web origin, a clock the tests drive (for the superseded
/// window), and one athlete per scenario so a password change or a pause cannot leak between tests.
/// Development, so the localhost rule of appsettings.Development.json applies as it does locally.
/// </summary>
public sealed class WebTransportApiFactory : ApiFactory
{
    public const string PwaOrigin = "https://pwa.beyondmovement.test";

    public const string WebAthlete = "web@nowhere.test";
    public const string LogoutAthlete = "web-logout@nowhere.test";
    public const string ChangeAthlete = "web-change@nowhere.test";
    public const string PauseAthlete = "web-pause@nowhere.test";
    public const string ResetAthlete = "web-reset@nowhere.test";

    public FixedClock Clock { get; } = new();
    public Guid PauseAthleteId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = PwaOrigin }));

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
        foreach (var email in new[] { WebAthlete, LogoutAthlete, ChangeAthlete, ResetAthlete })
            await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, email, "Web", "Tennis", new DateOnly(2000, 1, 1));

        PauseAthleteId = await AthleteApiFactory.AddAthleteAsync(
            db, scope.ServiceProvider, PauseAthlete, "Web", "Tennis", new DateOnly(2000, 1, 1));
    }
}

/// <summary>
/// Phase 4B: the same authentication, delivered two ways. Native sends no transport header and
/// gets the refresh token in JSON, exactly as before. The PWA sends <c>X-Token-Transport: cookie</c>
/// from a trusted origin and gets it only in an HttpOnly cookie.
/// <para>
/// Cookies are handled by hand - no cookie container - so every <c>Set-Cookie</c> the API sends is
/// asserted exactly, and every request carries exactly the cookie the test chose.
/// </para>
/// </summary>
public sealed class WebTokenTransportTests : IClassFixture<WebTransportApiFactory>
{
    private const string Pwa = WebTransportApiFactory.PwaOrigin;
    private const string Password = AthleteApiFactory.AthletePassword;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebTransportApiFactory _factory;

    public WebTokenTransportTests(WebTransportApiFactory factory)
    {
        _factory = factory;

        // Real time, on a whole millisecond (PostgreSQL keeps microseconds); one test at a time.
        var now = DateTime.UtcNow;
        _factory.Clock.UtcNow = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    // =========================================================== native, unchanged

    [Fact]
    public async Task Native_login_returns_the_refresh_token_in_json_and_sets_no_cookie()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete), transport: null, origin: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Native_refresh_and_logout_work_from_the_body_with_no_origin_and_no_cookie()
    {
        var login = await NativeLoginAsync(WebTransportApiFactory.WebAthlete);

        var refresh = await SendAsync(Post("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken }, transport: null, origin: null));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.False(refresh.Headers.Contains("Set-Cookie"));
        var rotated = (await refresh.Content.ReadFromJsonAsync<Tokens>(Json))!;
        Assert.False(string.IsNullOrEmpty(rotated.RefreshToken));

        var logout = await SendAsync(Post("/api/v1/auth/logout", new { refreshToken = rotated.RefreshToken },
            transport: null, origin: null, bearer: rotated.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(logout.Headers.Contains("Set-Cookie"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(
            Post("/api/v1/auth/refresh", new { refreshToken = rotated.RefreshToken }, transport: null, origin: null))).StatusCode);
    }

    /// <summary>Transport is opt-in by header alone - a browser Origin does not switch it.</summary>
    [Fact]
    public async Task A_browser_request_without_the_header_is_served_as_native()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete), transport: null, origin: Pwa));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await BodyAsync(response)).GetProperty("refreshToken").GetString()));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // =================================================================== web login

    [Fact]
    public async Task Cookie_login_sets_a_hardened_cookie_and_keeps_the_token_out_of_json()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("refreshToken").ValueKind);

        var cookie = RefreshCookieOf(response);
        AssertHardened(cookie);
        Assert.Equal((30 * 24 * 60 * 60).ToString(), cookie.Attributes["max-age"]);

        // The token in the cookie is a real, active refresh token - and appears nowhere in the JSON.
        var token = Uri.UnescapeDataString(cookie.Value);
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain(cookie.Value, raw);
        Assert.True(await IsActiveAsync(token));
    }

    [Fact]
    public async Task The_transport_value_is_matched_ignoring_case()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete), transport: "COOKIE"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHardened(RefreshCookieOf(response));
    }

    [Fact]
    public async Task Cookie_google_sign_in_sets_the_cookie()
    {
        _factory.GoogleValidator.NextIdentity = new GoogleIdentity(
            "google-web-subject", WebTransportApiFactory.WebAthlete, EmailVerified: true, "Web");

        var response = await SendAsync(Post("/api/v1/auth/google", new { idToken = "stub-google-token" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyAsync(response)).GetProperty("refreshToken").ValueKind);
        var cookie = RefreshCookieOf(response);
        AssertHardened(cookie);
        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(cookie.Value)));
    }

    [Fact]
    public async Task Cookie_registration_sets_the_cookie()
    {
        const string email = "web-register@nowhere.test";
        var registrationToken = await RegistrationTokenForAsync(email);

        var response = await SendAsync(Post("/api/v1/auth/register",
            new { registrationToken, password = "Web#Register-2026" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyAsync(response)).GetProperty("refreshToken").ValueKind);
        var cookie = RefreshCookieOf(response);
        AssertHardened(cookie);
        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(cookie.Value)));
    }

    // ================================================================= web refresh

    [Fact]
    public async Task Cookie_refresh_rotates_from_the_cookie_and_replaces_it()
    {
        var first = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);

        // No body at all: the token comes from the cookie.
        var response = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: first));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(raw).RootElement.GetProperty("refreshToken").ValueKind);

        var second = RefreshCookieOf(response);
        AssertHardened(second);
        Assert.NotEqual(first, second.Value);
        Assert.DoesNotContain(Uri.UnescapeDataString(second.Value), raw);
        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(second.Value)));
        Assert.False(await IsActiveAsync(Uri.UnescapeDataString(first)));

        // The old cookie moments later: superseded, and the browser's cookie is left alone.
        _factory.Clock.UtcNow += TimeSpan.FromSeconds(2);
        var duplicate = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: first));
        Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        Assert.Equal("REFRESH_SUPERSEDED", await ErrorCodeAsync(duplicate));
        Assert.Null(TryRefreshCookieOf(duplicate));

        // The old cookie later still: a replay. The family dies on the server; the response
        // leaves the browser's cookie alone, as every failed refresh does.
        _factory.Clock.UtcNow += TimeSpan.FromSeconds(10);
        var replay = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: first));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(replay));
        AssertNoSetCookie(replay);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: second.Value))).StatusCode);
    }

    /// <summary>A token in the body is not read in cookie mode, valid or not.</summary>
    [Fact]
    public async Task Cookie_refresh_ignores_a_refresh_token_in_the_body()
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);
        var native = await NativeLoginAsync(WebTransportApiFactory.WebAthlete);

        var response = await SendAsync(Post("/api/v1/auth/refresh", new { refreshToken = native.RefreshToken }, cookie: cookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await IsActiveAsync(Uri.UnescapeDataString(cookie)));
        Assert.True(await IsActiveAsync(native.RefreshToken!), "the body token must not have been spent");
    }

    [Fact]
    public async Task Cookie_refresh_without_a_cookie_is_invalid_and_sets_nothing()
    {
        var response = await SendAsync(Post("/api/v1/auth/refresh", body: new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>
    /// The multi-tab case a failed refresh must not break. Tab B's refresh leaves carrying an old,
    /// dead cookie; meanwhile tab A signs in again and the browser stores the new cookie. B's 401
    /// must not expire anything - a deletion would remove A's new cookie, not B's old one.
    /// </summary>
    [Fact]
    public async Task A_stale_refresh_cannot_clobber_a_newer_sign_in_cookie()
    {
        var (old, access) = await CookieLoginWithAccessAsync(WebTransportApiFactory.WebAthlete);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(Post("/api/v1/auth/logout", body: null, cookie: old, bearer: access))).StatusCode);

        var newer = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);

        var stale = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: old));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(stale));
        AssertNoSetCookie(stale);

        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(newer)));
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: newer))).StatusCode);
    }

    [Fact]
    public async Task A_garbage_cookie_is_invalid_and_sets_nothing()
    {
        var response = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: "not-a-refresh-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(response));
        AssertNoSetCookie(response);
    }

    /// <summary>Left in the browser, an expired cookie stays useless: validity lives on the server.</summary>
    [Fact]
    public async Task An_expired_cookie_is_invalid_sets_nothing_and_stays_unusable()
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);

        _factory.Clock.UtcNow += TimeSpan.FromDays(31);

        var first = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(first));
        AssertNoSetCookie(first);

        Assert.False(await IsActiveAsync(Uri.UnescapeDataString(cookie)));
        var again = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(again));
        AssertNoSetCookie(again);
    }

    /// <summary>
    /// Phase 4A's race through the cookie: several tabs refreshing with the same cookie at once.
    /// One rotates and sets the new cookie; the rest are superseded and must not touch the cookie -
    /// an expiry from any of them would delete the winner's valid replacement.
    /// </summary>
    [Fact]
    public async Task Concurrent_cookie_refreshes_set_one_cookie_and_the_losers_leave_it_alone()
    {
        const int Requests = 10;
        var cookie = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);
        var client = Client();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, Requests).Select(async _ =>
        {
            await gate.Task;
            return await client.SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        }).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r != winner))
        {
            Assert.Equal("REFRESH_SUPERSEDED", await ErrorCodeAsync(loser));
            Assert.False(loser.Headers.Contains("Set-Cookie"), "a superseded refresh must not set or expire the cookie");
        }

        var replacement = RefreshCookieOf(winner);
        AssertHardened(replacement);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: replacement.Value))).StatusCode);
    }

    // ================================================================== web logout

    [Fact]
    public async Task Cookie_logout_ends_that_session_expires_the_cookie_and_spares_other_sign_ins()
    {
        var other = await NativeLoginAsync(WebTransportApiFactory.LogoutAthlete);
        var (cookie, access) = await CookieLoginWithAccessAsync(WebTransportApiFactory.LogoutAthlete);

        var logout = await SendAsync(Post("/api/v1/auth/logout", body: null, cookie: cookie, bearer: access));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertExpired(RefreshCookieOf(logout));
        Assert.False(await IsActiveAsync(Uri.UnescapeDataString(cookie)));

        var stale = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(stale));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(
            Post("/api/v1/auth/refresh", new { refreshToken = other.RefreshToken }, transport: null, origin: null))).StatusCode);
    }

    [Fact]
    public async Task Cookie_logout_without_a_cookie_still_signs_the_browser_out()
    {
        var (_, access) = await CookieLoginWithAccessAsync(WebTransportApiFactory.LogoutAthlete);

        var logout = await SendAsync(Post("/api/v1/auth/logout", body: null, bearer: access));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertExpired(RefreshCookieOf(logout));
    }

    // ================================================================ CSRF / origin

    public static TheoryData<string?> UntrustedOrigins => new()
    {
        "https://evil.example.test",
        "https://pwa.beyondmovement.test.evil.test",
        "http://pwa.beyondmovement.test",
        "https://pwa.beyondmovement.test:8443",
        "null",
        null
    };

    /// <summary>
    /// Another site - or a request with no Origin at all - cannot spend the athlete's cookie. The
    /// request is refused before the token is touched, and nothing is set.
    /// </summary>
    [Theory]
    [MemberData(nameof(UntrustedOrigins))]
    public async Task Cookie_refresh_from_an_untrusted_or_missing_origin_is_refused(string? origin)
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);

        var forged = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie, origin: origin));

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED", await ErrorCodeAsync(forged));
        Assert.False(forged.Headers.Contains("Set-Cookie"));
        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(cookie)), "a refused request must not spend the token");
    }

    [Theory]
    [MemberData(nameof(UntrustedOrigins))]
    public async Task Cookie_logout_from_an_untrusted_or_missing_origin_is_refused(string? origin)
    {
        var (cookie, access) = await CookieLoginWithAccessAsync(WebTransportApiFactory.LogoutAthlete);

        var forged = await SendAsync(Post("/api/v1/auth/logout", body: null, cookie: cookie, bearer: access, origin: origin));

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED", await ErrorCodeAsync(forged));
        Assert.False(forged.Headers.Contains("Set-Cookie"));
        Assert.True(await IsActiveAsync(Uri.UnescapeDataString(cookie)), "a refused logout must not revoke anything");
    }

    [Fact]
    public async Task Cookie_login_from_an_untrusted_origin_is_refused_and_sets_no_cookie()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete),
            origin: "https://evil.example.test"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED", await ErrorCodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // An EMPTY header is refused too (TokenTransport checks presence, not the bound value), but the
    // in-memory test server drops empty headers before the app sees them, so that case is not
    // expressible here; it was checked against Kestrel directly.
    [Theory]
    [InlineData("body")]
    [InlineData("bearer")]
    [InlineData("cookies")]
    [InlineData("cookie, cookie")]
    public async Task An_unsupported_transport_value_is_refused_rather_than_ignored(string value)
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete), transport: value));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("TOKEN_TRANSPORT_UNSUPPORTED", await ErrorCodeAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>The cookie is a refresh credential only. Ordinary endpoints still need the bearer token.</summary>
    [Fact]
    public async Task An_ordinary_endpoint_does_not_authenticate_from_the_cookie()
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.WebAthlete);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Origin", Pwa);
        request.Headers.Add(TokenTransport.HeaderName, TokenTransport.CookieValue);
        request.Headers.Add("Cookie", $"{RefreshCookie.Name}={cookie}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(request)).StatusCode);
    }

    // ============================================================ local development

    /// <summary><c>flutter run -d chrome</c> serves the PWA from a localhost port that changes every run.</summary>
    [Fact]
    public async Task In_development_a_localhost_origin_can_use_cookie_transport()
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete),
            origin: "http://localhost:61234"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHardened(RefreshCookieOf(response));
    }

    /// <summary>
    /// Production with the real PWA origin configured: it works, and localhost - trusted only in
    /// Development - is refused, by the same origin list CORS uses.
    /// </summary>
    [Fact]
    public async Task In_production_only_the_configured_pwa_origin_can_use_cookie_transport()
    {
        const string production = "https://app.beyondmovementbyn.com";
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = production }));
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var trusted = await client.SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete), origin: production));
        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        AssertHardened(RefreshCookieOf(trusted));

        var localhost = await client.SendAsync(Post("/api/v1/auth/login", Credentials(WebTransportApiFactory.WebAthlete),
            origin: "http://localhost:61234"));
        Assert.Equal(HttpStatusCode.Forbidden, localhost.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED", await ErrorCodeAsync(localhost));
    }

    // ===================================================== session invalidation

    [Fact]
    public async Task Cookie_password_change_expires_the_cookie_and_the_old_cookie_is_dead()
    {
        var (cookie, access) = await CookieLoginWithAccessAsync(WebTransportApiFactory.ChangeAthlete);

        var change = await SendAsync(Post("/api/v1/auth/change-password",
            new { currentPassword = Password, newPassword = "Web#Changed-2026" }, bearer: access));

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        AssertExpired(RefreshCookieOf(change));

        // A browser that kept the cookie anyway cannot restore the session.
        var stale = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(stale));
        AssertNoSetCookie(stale);
    }

    /// <summary>The Admin pauses from their own browser; the athlete's cookie dies on the server.</summary>
    [Fact]
    public async Task A_paused_athletes_cookie_cannot_restore_the_session()
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.PauseAthlete);
        var admin = await NativeLoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

        var pause = await SendAsync(Post($"/api/v1/athletes/{_factory.PauseAthleteId}/pause", body: null,
            transport: null, origin: null, bearer: admin.AccessToken));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        var stale = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        AssertNoSetCookie(stale);
    }

    /// <summary>
    /// A reset is often done on another device, with no browser session to tidy: the server
    /// revokes, and the browser's stale cookie then simply fails. Done in the same browser with the
    /// header, the reset response expires the cookie itself.
    /// </summary>
    [Fact]
    public async Task A_password_reset_kills_the_cookie_session_on_the_server_and_tidies_this_browser_if_asked()
    {
        var cookie = await CookieLoginAsync(WebTransportApiFactory.ResetAthlete);

        var elsewhere = await SendAsync(Post("/api/v1/auth/reset-password",
            new { token = await ResetTokenAsync(WebTransportApiFactory.ResetAthlete), newPassword = Password },
            transport: null, origin: null));
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        Assert.False(elsewhere.Headers.Contains("Set-Cookie"));

        var stale = await SendAsync(Post("/api/v1/auth/refresh", body: null, cookie: cookie));
        Assert.Equal("INVALID_REFRESH_TOKEN", await ErrorCodeAsync(stale));
        AssertNoSetCookie(stale);

        var here = await SendAsync(Post("/api/v1/auth/reset-password",
            new { token = await ResetTokenAsync(WebTransportApiFactory.ResetAthlete), newPassword = Password }));
        Assert.Equal(HttpStatusCode.OK, here.StatusCode);
        AssertExpired(RefreshCookieOf(here));
    }

    // ===================================================================== helpers

    private sealed record Tokens(string AccessToken, string? RefreshToken);

    private sealed record SetCookie(string Value, Dictionary<string, string> Attributes);

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Client().SendAsync(request);

    private static object Credentials(string email, string password = Password) => new { email, password };

    /// <summary>A POST as the PWA sends it by default: cookie transport, from the trusted origin.</summary>
    private static HttpRequestMessage Post(string path, object? body, string? cookie = null, string? bearer = null,
        string? transport = TokenTransport.CookieValue, string? origin = Pwa)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (transport is not null)
            request.Headers.TryAddWithoutValidation(TokenTransport.HeaderName, transport);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", $"{RefreshCookie.Name}={cookie}");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private async Task<Tokens> NativeLoginAsync(string email, string password = Password)
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(email, password), transport: null, origin: null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Tokens>(Json))!;
    }

    /// <summary>The cookie value exactly as the browser would store and send it back.</summary>
    private async Task<string> CookieLoginAsync(string email) => (await CookieLoginWithAccessAsync(email)).Cookie;

    private async Task<(string Cookie, string AccessToken)> CookieLoginWithAccessAsync(string email)
    {
        var response = await SendAsync(Post("/api/v1/auth/login", Credentials(email)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<Tokens>(Json))!;
        Assert.Null(tokens.RefreshToken);
        return (RefreshCookieOf(response).Value, tokens.AccessToken);
    }

    private static SetCookie? TryRefreshCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var lines))
            return null;

        var line = lines.SingleOrDefault(l => l.StartsWith(RefreshCookie.Name + "=", StringComparison.Ordinal));
        if (line is null)
            return null;

        var parts = line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var value = parts[0][(RefreshCookie.Name.Length + 1)..];
        var attributes = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : "", StringComparer.OrdinalIgnoreCase);

        return new SetCookie(value, attributes);
    }

    private static SetCookie RefreshCookieOf(HttpResponseMessage response) =>
        TryRefreshCookieOf(response) ?? throw new Xunit.Sdk.XunitException("Expected a Set-Cookie for the refresh cookie.");

    /// <summary>Every attribute that keeps the cookie away from scripts, other sites and other hosts.</summary>
    private static void AssertHardened(SetCookie cookie)
    {
        Assert.False(string.IsNullOrEmpty(cookie.Value));
        Assert.True(cookie.Attributes.ContainsKey("httponly"), "HttpOnly");
        Assert.True(cookie.Attributes.ContainsKey("secure"), "Secure");
        Assert.Equal("strict", cookie.Attributes["samesite"], ignoreCase: true);
        Assert.Equal("/api/v1/auth", cookie.Attributes["path"]);
        Assert.False(cookie.Attributes.ContainsKey("domain"), "host-only: no Domain attribute");
        Assert.True(cookie.Attributes.ContainsKey("max-age"), "Max-Age");
    }

    /// <summary>A failed refresh leaves the browser's cookie exactly as it is: no Set-Cookie of any kind.</summary>
    private static void AssertNoSetCookie(HttpResponseMessage response) =>
        Assert.False(response.Headers.Contains("Set-Cookie"), "a failed refresh must not set or expire the cookie");

    /// <summary>An expiry the browser will honour: empty, in the past, same Path and flags as when set.</summary>
    private static void AssertExpired(SetCookie cookie)
    {
        Assert.Equal("", cookie.Value);
        Assert.True(cookie.Attributes.TryGetValue("expires", out var expires)
                    && DateTimeOffset.Parse(expires) < DateTimeOffset.UtcNow, "expired in the past");
        Assert.Equal("/api/v1/auth", cookie.Attributes["path"]);
        Assert.True(cookie.Attributes.ContainsKey("secure"));
        Assert.True(cookie.Attributes.ContainsKey("httponly"));
        Assert.Equal("strict", cookie.Attributes["samesite"], ignoreCase: true);
        Assert.False(cookie.Attributes.ContainsKey("domain"));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).GetProperty("errorCode").GetString();

    private async Task<bool> IsActiveAsync(string rawToken)
    {
        using var scope = _factory.Services.CreateScope();
        var hash = scope.ServiceProvider.GetRequiredService<ITokenService>().Hash(rawToken);
        var token = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash);
        return token is not null && token.IsActive(_factory.Clock.UtcNow);
    }

    private async Task<string> ResetTokenAsync(string email)
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(
            Post("/api/v1/auth/forgot-password", new { email }, transport: null, origin: null))).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var mail = scope.ServiceProvider.GetRequiredService<TestEmailOutbox>().Messages.Last(m => m.To == email);
        return Uri.UnescapeDataString(Regex.Match(mail.TextBody, @"token=([^\s""&<]+)").Groups[1].Value);
    }

    /// <summary>Invite, read the code from the outbox as the athlete would, validate it.</summary>
    private async Task<string> RegistrationTokenForAsync(string email)
    {
        var admin = await NativeLoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var invite = await SendAsync(Post("/api/v1/invitations", new { email }, transport: null, origin: null, bearer: admin.AccessToken));
        Assert.True(invite.IsSuccessStatusCode, $"invite answered {(int)invite.StatusCode}");

        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var codeHash = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invitations.AsNoTracking()
            .SingleAsync(i => i.Email == email)).CodeHash;
        var code = scope.ServiceProvider.GetRequiredService<TestEmailOutbox>().Messages
            .SelectMany(m => m.TextBody.Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(word => word.Trim())
            .First(word => tokens.Hash(InvitationCode.Normalize(word)) == codeHash);

        var validated = await Client().GetAsync($"/api/v1/invitations/validate?code={Uri.EscapeDataString(code)}");
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        return (await BodyAsync(validated)).GetProperty("registrationToken").GetString()!;
    }
}
