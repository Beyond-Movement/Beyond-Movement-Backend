using BeyondMovement.Modules.Identity;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Api.Authentication;

/// <summary>How a refresh token travels between the API and one client.</summary>
public enum TokenTransportMode
{
    /// <summary>In the JSON body, both ways. The native apps, and the default.</summary>
    Body,

    /// <summary>In the <see cref="RefreshCookie"/> only, never in JSON. The PWA, by explicit opt-in.</summary>
    Cookie
}

/// <summary>
/// Chooses the refresh-token transport for one request, and applies it.
/// <para>
/// <b>Opt-in only.</b> Cookie transport is used when, and only when, the request carries
/// <c>X-Token-Transport: cookie</c>. Nothing is inferred from <c>Origin</c>, the user agent or
/// anything else about the caller, so a native app that sends no such header can never be switched
/// into it. No header means <see cref="TokenTransportMode.Body"/>, exactly as before this existed.
/// </para>
/// <para>
/// <b>Deterministic.</b> Any other value - empty, <c>body</c>, a typo, the header twice - is
/// refused with <c>400 TOKEN_TRANSPORT_UNSUPPORTED</c> rather than quietly falling back, because a
/// client that asked for something specific and silently got another transport would mishandle
/// its credentials. The value is matched ignoring case, as HTTP tokens are.
/// </para>
/// <para>
/// <b>Cookie transport requires a trusted <c>Origin</c></b> - one of <see cref="TrustedWebOrigins"/>,
/// the list CORS answers to. A missing, <c>null</c> or foreign origin is refused with
/// <c>403 ORIGIN_NOT_ALLOWED</c> before any work is done. Browsers always send <c>Origin</c> on the
/// cross-origin requests the PWA makes, so only a forged or foreign request lacks a trusted one.
/// This is the CSRF check: together with the custom header (which forces a CORS preflight) and the
/// cookie's <c>SameSite=Strict</c>, another site cannot make the browser present the athlete's
/// refresh cookie, and could not read the answer if it did.
/// </para>
/// </summary>
public sealed class TokenTransport(TrustedWebOrigins origins)
{
    public const string HeaderName = "X-Token-Transport";
    public const string CookieValue = "cookie";

    /// <summary>Appended to the description of every endpoint that honours the header.</summary>
    public const string ContractDescription =
        "TOKEN TRANSPORT: native apps send no X-Token-Transport header and nothing changes - the " +
        "refresh token travels in the JSON body. The web app sends X-Token-Transport: cookie on " +
        "a credentialed request from a trusted origin: the refresh token then travels ONLY in the " +
        "HttpOnly cookie __Secure-bm_refresh (Path=/api/v1/auth), refreshToken in a JSON response " +
        "is null, and a refreshToken in the request body is ignored. Any other header value is 400 " +
        "TOKEN_TRANSPORT_UNSUPPORTED; cookie transport from a missing or untrusted Origin is 403 " +
        "ORIGIN_NOT_ALLOWED.";

    /// <param name="headerValue">The bound header, several values joined with commas.</param>
    public Result<TokenTransportMode> Resolve(string? headerValue, HttpContext http)
    {
        // Presence, not the bound value: binding turns an empty header into null, and an empty
        // header is a request for something unsupported, not the absence of one.
        if (!http.Request.Headers.ContainsKey(HeaderName))
            return Result<TokenTransportMode>.Success(TokenTransportMode.Body);

        if (!string.Equals(headerValue?.Trim(), CookieValue, StringComparison.OrdinalIgnoreCase))
            return Result<TokenTransportMode>.Failure(IdentityErrors.TokenTransportUnsupported);

        if (!origins.IsTrusted(http.Request.Headers.Origin.ToString()))
            return Result<TokenTransportMode>.Failure(IdentityErrors.OriginNotAllowed);

        return Result<TokenTransportMode>.Success(TokenTransportMode.Cookie);
    }

    /// <summary>
    /// The successful authentication response as this transport delivers it. For the cookie the
    /// raw refresh token is moved into <see cref="RefreshCookie"/> and removed from the body.
    /// </summary>
    public static AuthResponse Deliver(HttpContext http, TokenTransportMode mode, AuthResponse response)
    {
        if (mode == TokenTransportMode.Body)
            return response;

        RefreshCookie.Write(http.Response, response.RefreshToken!,
            TimeSpan.FromSeconds(response.RefreshExpiresInSeconds));

        return response with { RefreshToken = null };
    }

    /// <summary>The refresh token this request presents: the cookie in cookie mode, the body otherwise.</summary>
    public static string? PresentedRefreshToken(HttpContext http, TokenTransportMode mode, string? bodyToken) =>
        mode == TokenTransportMode.Cookie ? RefreshCookie.Read(http.Request) : bodyToken;

    /// <summary>Expires the browser's cookie when the session it held is over. Nothing in body mode.</summary>
    public static void EndBrowserSession(HttpContext http, TokenTransportMode mode)
    {
        if (mode == TokenTransportMode.Cookie)
            RefreshCookie.Expire(http.Response);
    }
}

/// <summary>
/// The PWA's refresh token, held by the browser for the API and unreadable by any script.
/// <list type="bullet">
/// <item><c>__Secure-</c> prefix: the browser accepts the cookie only with <c>Secure</c>, set from a
/// secure origin. (<c>__Host-</c> would also forbid a narrower <c>Path</c>, which matters more.)</item>
/// <item><c>HttpOnly</c>: invisible to JavaScript, so a script on the PWA cannot read or exfiltrate it.</item>
/// <item><c>Secure</c>: HTTPS only. Browsers treat <c>http://localhost</c> as secure, so local
/// development needs no weaker setting.</item>
/// <item><c>SameSite=Strict</c>: sent only on same-site requests. <c>app.</c> and <c>api.</c> under
/// one registrable domain are same-site, so the PWA's calls carry it; no other site's do.</item>
/// <item>No <c>Domain</c>: host-only. The cookie belongs to the API host alone and is never sent to
/// the PWA host or any other subdomain.</item>
/// <item><c>Path=/api/v1/auth</c>: sent only to the auth endpoints, never to ordinary API calls -
/// which do not read it anyway.</item>
/// <item><c>Max-Age</c>: the refresh token's own remaining lifetime, renewed on every rotation, so
/// the cookie never outlives the token it carries.</item>
/// </list>
/// Setting and expiring share <see cref="Options"/>, so they cannot drift apart: a deletion with a
/// different <c>Path</c> would leave the original cookie in place.
/// </summary>
public static class RefreshCookie
{
    public const string Name = "__Secure-bm_refresh";
    public const string Path = "/api/v1/auth";

    public static void Write(HttpResponse response, string refreshToken, TimeSpan lifetime) =>
        response.Cookies.Append(Name, refreshToken, Options(lifetime));

    public static void Expire(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(maxAge: null));

    public static string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = maxAge,

        // Strictly necessary - it is the sign-in itself - so consent tooling must not drop it.
        IsEssential = true
    };
}
