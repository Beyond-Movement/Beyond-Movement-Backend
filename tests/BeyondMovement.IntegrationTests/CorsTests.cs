using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The app as a deployed PWA environment would configure it: one exact origin in
/// <c>Cors:AllowedOrigins</c>. Development, so <c>appsettings.Development.json</c>'s localhost rule
/// is in force too - exactly what a developer running Flutter Web locally gets.
/// </summary>
public sealed class WebClientApiFactory : ApiFactory
{
    public const string PwaOrigin = "https://pwa.beyondmovement.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = PwaOrigin
            }));
    }
}

/// <summary>
/// CORS for the Flutter Web client, through the real pipeline: the same middleware order,
/// authentication and fallback policy the PWA meets. CORS is enforced by the browser, so what is
/// asserted is what the browser decides on - the <c>Access-Control-*</c> response headers.
/// </summary>
public sealed class CorsTests(WebClientApiFactory factory) : IClassFixture<WebClientApiFactory>
{
    private const string Pwa = WebClientApiFactory.PwaOrigin;
    private const string Stranger = "https://evil.example.test";

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    // ------------------------------------------------------------ actual requests

    [Fact]
    public async Task The_configured_origin_is_allowed_on_a_simple_request()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/ping", Pwa);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
        Assert.Contains("Origin", response.Headers.Vary);

        // Credentialed, so the PWA's auth calls can carry the HttpOnly refresh cookie.
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));

        // The client reads retryAfterSeconds and correlationId from the body; nothing is exposed.
        Assert.False(response.Headers.Contains("Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task An_unknown_origin_gets_no_cors_headers()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/ping", Stranger);

        // The server still answers - CORS is not authentication - but without the header the
        // browser refuses to hand the response to the page.
        Assert.Null(AllowOrigin(response));
    }

    [Fact]
    public async Task A_lookalike_origin_is_not_the_configured_one()
    {
        Assert.Null(AllowOrigin(await SendAsync(HttpMethod.Get, "/api/v1/ping", "https://pwa.beyondmovement.test.evil.test")));
        Assert.Null(AllowOrigin(await SendAsync(HttpMethod.Get, "/api/v1/ping", "http://pwa.beyondmovement.test")));
        Assert.Null(AllowOrigin(await SendAsync(HttpMethod.Get, "/api/v1/ping", "https://pwa.beyondmovement.test:8443")));
        Assert.Null(AllowOrigin(await SendAsync(HttpMethod.Get, "/api/v1/ping", "null")));
    }

    [Fact]
    public async Task A_native_request_with_no_origin_is_untouched()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(AllowOrigin(response));
    }

    [Fact]
    public async Task An_authenticated_request_with_a_bearer_token_is_allowed()
    {
        var token = await AdminTokenAsync();
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", Pwa, request =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
    }

    /// <summary>
    /// The client refreshes its token when it sees 401. If the 401 carried no CORS header the
    /// browser would report an opaque network error instead, and the refresh would never run.
    /// </summary>
    [Fact]
    public async Task A_401_is_readable_by_the_pwa()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", Pwa);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
    }

    /// <summary>
    /// Malformed JSON goes through the exception handler, which clears response headers when it
    /// writes the problem body. The CORS header must survive that.
    /// </summary>
    [Fact]
    public async Task An_error_rewritten_by_the_exception_handler_is_readable_by_the_pwa()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", Pwa, request =>
            request.Content = new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
    }

    // ---------------------------------------------------------------- preflight

    /// <summary>Login: anonymous, JSON body, and the correlation id every request carries.</summary>
    [Fact]
    public async Task Preflight_for_login_succeeds()
    {
        var response = await PreflightAsync("/api/v1/auth/login", Pwa, "POST", "content-type,x-correlation-id");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
        Assert.Contains("POST", AllowMethods(response));
        Assert.Contains("content-type", AllowHeaders(response));
        Assert.Contains("x-correlation-id", AllowHeaders(response));
        Assert.Equal("600", Single(response, "Access-Control-Max-Age"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
    }

    /// <summary>
    /// What the PWA sends before a cookie-transport call: credentials and X-Token-Transport.
    /// A trusted origin is told both are fine; anyone else gets neither.
    /// </summary>
    [Fact]
    public async Task Credentialed_preflight_with_the_transport_header_is_allowed_only_for_a_trusted_origin()
    {
        var trusted = await PreflightAsync("/api/v1/auth/refresh", Pwa, "POST", "content-type,x-correlation-id,x-token-transport");

        Assert.Equal(HttpStatusCode.NoContent, trusted.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(trusted));
        Assert.Equal("true", Single(trusted, "Access-Control-Allow-Credentials"));
        Assert.Contains("x-token-transport", AllowHeaders(trusted));

        var stranger = await PreflightAsync("/api/v1/auth/refresh", Stranger, "POST", "content-type,x-token-transport");

        Assert.Null(AllowOrigin(stranger));
        Assert.False(stranger.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    /// <summary>
    /// A preflight never carries the token, so it must be answered before authentication and the
    /// deny-by-default fallback policy - not refused with 401.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/me", "GET")]
    [InlineData("/api/v1/auth/me/timezone", "PUT")]
    [InlineData("/api/v1/expenses/00000000-0000-0000-0000-000000000001", "DELETE")]
    public async Task Preflight_for_an_authenticated_endpoint_is_not_refused_by_authentication(string path, string method)
    {
        var response = await PreflightAsync(path, Pwa, method, "authorization,content-type,x-correlation-id");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
        Assert.Contains(method, AllowMethods(response));
        Assert.Contains("authorization", AllowHeaders(response));
    }

    /// <summary>Booking is the one call that sends Idempotency-Key.</summary>
    [Fact]
    public async Task Preflight_for_booking_allows_the_idempotency_key()
    {
        var response = await PreflightAsync("/api/v1/scheduling/bookings", Pwa, "POST",
            "authorization,content-type,idempotency-key,x-correlation-id");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Pwa, AllowOrigin(response));
        Assert.Contains("idempotency-key", AllowHeaders(response));
    }

    [Fact]
    public async Task Preflight_from_an_unknown_origin_is_not_allowed()
    {
        var response = await PreflightAsync("/api/v1/auth/login", Stranger, "POST", "content-type");

        Assert.Null(AllowOrigin(response));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    /// <summary>
    /// The answer lists what IS allowed; the browser compares the request against it and blocks
    /// anything missing. So "not allowed" means "not in the list", even for a known origin.
    /// </summary>
    [Fact]
    public async Task Preflight_for_a_header_the_client_never_sends_is_not_allowed()
    {
        var response = await PreflightAsync("/api/v1/auth/me", Pwa, "GET", "authorization,x-something-else");

        Assert.Equal(["authorization", "content-type", "idempotency-key", "x-correlation-id", "x-token-transport"],
            AllowHeaders(response));
    }

    [Fact]
    public async Task Preflight_for_a_method_the_api_does_not_use_is_not_allowed()
    {
        var response = await PreflightAsync("/api/v1/auth/me/profile", Pwa, "PATCH", "authorization");

        Assert.Equal(["GET", "POST", "PUT", "DELETE"], AllowMethods(response));
    }

    // ------------------------------------------------- local development (Flutter Web)

    /// <summary><c>flutter run -d chrome</c> picks a new port each run.</summary>
    [Theory]
    [InlineData("http://localhost:61234")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:52000")]
    [InlineData("http://[::1]:52000")]
    [InlineData("https://localhost:7000")]
    public async Task In_development_localhost_on_any_port_is_allowed(string origin)
    {
        var response = await PreflightAsync("/api/v1/auth/login", origin, "POST", "content-type,x-correlation-id");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, AllowOrigin(response));
    }

    [Theory]
    [InlineData("http://localhost.evil.test")]
    [InlineData("http://127.0.0.1.nip.io:8080")]
    [InlineData("http://192.168.1.20:8080")]
    public async Task In_development_only_loopback_counts_as_localhost(string origin)
    {
        Assert.Null(AllowOrigin(await PreflightAsync("/api/v1/auth/login", origin, "POST", "content-type")));
    }

    // ---------------------------------------------------------------- production

    /// <summary>
    /// Production with an origin configured, as ECS will supply it. The development localhost
    /// rule must not leak in, and nothing but the exact origin is allowed.
    /// </summary>
    [Fact]
    public async Task In_production_only_the_configured_origin_is_allowed()
    {
        using var production = Production(new() { ["Cors:AllowedOrigins:0"] = "https://app.beyondmovement.test" });
        var client = production.CreateClient();

        Assert.Equal("https://app.beyondmovement.test",
            AllowOrigin(await PreflightAsync(client, "/api/v1/auth/login", "https://app.beyondmovement.test", "POST", "content-type")));

        Assert.Null(AllowOrigin(await PreflightAsync(client, "/api/v1/auth/login", "http://localhost:8080", "POST", "content-type")));
        Assert.Null(AllowOrigin(await PreflightAsync(client, "/api/v1/auth/login", Stranger, "POST", "content-type")));
        Assert.Null(AllowOrigin(await SendAsync(client, HttpMethod.Get, "/api/v1/ping", Stranger)));
    }

    // -------------------------------------------------------- startup validation

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.beyondmovement.test")]
    [InlineData("https://app.beyondmovement.test/")]
    [InlineData("https://app.beyondmovement.test/app")]
    [InlineData("https://app.beyondmovement.test?x=1")]
    [InlineData("app.beyondmovement.test")]
    [InlineData("ftp://app.beyondmovement.test")]
    public void A_malformed_or_wildcard_origin_fails_at_startup(string origin)
    {
        using var bad = Production(new() { ["Cors:AllowedOrigins:0"] = origin });

        var error = Record.Exception(() => bad.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Cors:AllowedOrigins", Flatten(error));
    }

    [Fact]
    public void The_localhost_rule_outside_development_fails_at_startup()
    {
        using var bad = Production(new() { ["Cors:AllowLocalhostOrigins"] = "true" });

        var error = Record.Exception(() => bad.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Cors:AllowLocalhostOrigins", Flatten(error));
    }

    [Fact]
    public void A_plain_http_origin_outside_development_fails_at_startup()
    {
        using var bad = Production(new() { ["Cors:AllowedOrigins:0"] = "http://app.beyondmovement.test" });

        var error = Record.Exception(() => bad.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("https", Flatten(error));
    }

    // ------------------------------------------------------------------ helpers

    private WebApplicationFactory<Program> Production(Dictionary<string, string?> settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        });

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string origin,
        Action<HttpRequestMessage>? configure = null) =>
        SendAsync(factory.CreateClient(), method, path, origin, configure);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string origin,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", origin);
        configure?.Invoke(request);
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PreflightAsync(string path, string origin, string method, string headers) =>
        PreflightAsync(factory.CreateClient(), path, origin, method, headers);

    /// <summary>What a browser sends before any request that is not "simple".</summary>
    private static Task<HttpResponseMessage> PreflightAsync(HttpClient client, string path, string origin,
        string method, string headers) =>
        SendAsync(client, HttpMethod.Options, path, origin, request =>
        {
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", headers);
        });

    private static string? AllowOrigin(HttpResponseMessage response) =>
        Single(response, "Access-Control-Allow-Origin");

    private static string[] AllowMethods(HttpResponseMessage response) => List(response, "Access-Control-Allow-Methods");

    private static string[] AllowHeaders(HttpResponseMessage response) =>
        List(response, "Access-Control-Allow-Headers").Select(x => x.ToLowerInvariant()).ToArray();

    private static string? Single(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static string[] List(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.SelectMany(x => x.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];

    private static string Flatten(Exception e)
    {
        var messages = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException) messages.Add(x.Message);
        if (e is AggregateException agg) messages.AddRange(agg.Flatten().InnerExceptions.Select(x => x.Message));
        return string.Join(" | ", messages);
    }

    private async Task<string> AdminTokenAsync()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AccessToken;
    }
}

/// <summary>
/// What appsettings.json ships, with no origin added by a fixture: in production that allows no
/// browser origin at all, so a deployment that forgets Cors__AllowedOrigins__0 fails closed.
/// </summary>
public sealed class ShippedCorsConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task In_production_the_shipped_configuration_allows_no_origin()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient();

        foreach (var origin in new[] { WebClientApiFactory.PwaOrigin, "https://evil.example.test", "http://localhost:8080", "null" })
        {
            var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
            Assert.False((await client.SendAsync(preflight)).Headers.Contains("Access-Control-Allow-Origin"));

            var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
            get.Headers.Add("Origin", origin);
            Assert.False((await client.SendAsync(get)).Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
}
