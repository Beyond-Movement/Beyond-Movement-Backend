using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Api;

/// <summary>
/// Which browser origins may call the API (the <c>Cors</c> configuration section). Only the
/// Flutter Web build (the PWA) needs this; the native apps send no <c>Origin</c> and CORS never
/// applies to them.
/// <para>
/// <b>Origins are a deployment choice, not an application one.</b> <see cref="AllowedOrigins"/>
/// ships empty and each environment supplies its own (<c>Cors__AllowedOrigins__0</c>,
/// <c>Cors__AllowedOrigins__1</c>, ...), the same way <c>Storage__S3__BucketName</c> works. Left
/// empty, no browser origin is allowed and everything else is unaffected.
/// </para>
/// </summary>
public sealed class WebClientCorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins: scheme, host and port, nothing else - <c>https://app.example.com</c>, not
    /// <c>https://app.example.com/</c> and never <c>*</c>. Checked at startup. Outside
    /// Development they must be https, because a PWA cannot run anywhere else.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Allow <c>http(s)://localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c> on ANY port.
    /// <c>flutter run -d chrome</c> picks a new port every run unless given <c>--web-port</c>,
    /// so an exact list cannot keep up. Development only: true in any other environment stops the
    /// app at startup, so it can never widen a deployed policy.
    /// </summary>
    public bool AllowLocalhostOrigins { get; set; }
}

public static class WebClientCors
{
    public const string PolicyName = "web-client";

    /// <summary>
    /// What the Flutter client actually sends, read from its Dio setup rather than guessed. OPTIONS
    /// is not listed: it is the preflight itself, answered by the middleware. There is no PATCH
    /// endpoint, so PATCH is not allowed either.
    /// </summary>
    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE"];

    /// <summary>
    /// Headers beyond the CORS safelist that the client sends. <c>Accept</c> is safelisted and
    /// needs no entry; <c>Content-Type: application/json</c> is not, which is why every write is
    /// preflighted. <c>X-Correlation-ID</c> is sent on every request by the client's interceptor,
    /// so without it here every preflight would fail.
    /// </summary>
    private static readonly string[] Headers = ["Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-ID"];

    /// <summary>
    /// How long a browser may reuse a preflight answer. Each authenticated request is otherwise
    /// two round trips. Chrome caps this at two hours anyway.
    /// </summary>
    private static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// No response headers are exposed: the client reads <c>retryAfterSeconds</c> and
    /// <c>correlationId</c> from the problem body, never from <c>Retry-After</c>.
    /// <para>
    /// Credentials are NOT allowed. Tokens travel in the <c>Authorization</c> header, which CORS
    /// permits without credentials mode. A future HttpOnly refresh cookie needs
    /// <c>.AllowCredentials()</c> added below, and nothing else: origins are already an exact list,
    /// which credentials mode requires.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWebClientCors(this IServiceCollection services)
    {
        services.AddOptions<WebClientCorsOptions>()
            .BindConfiguration(WebClientCorsOptions.SectionName)
            .Validate(o => o.AllowedOrigins.All(x => TryNormalize(x, out _)),
                "Cors:AllowedOrigins entries must be exact origins - scheme, host and optional port, " +
                "with no path, query, trailing slash or wildcard - e.g. https://app.example.com.")
            .Validate<IHostEnvironment>((o, env) => env.IsDevelopment() || !o.AllowLocalhostOrigins,
                "Cors:AllowLocalhostOrigins is for Development only. Remove it (Cors__AllowLocalhostOrigins) " +
                "and list the PWA's origin in Cors:AllowedOrigins instead.")
            .Validate<IHostEnvironment>((o, env) => env.IsDevelopment()
                    || o.AllowedOrigins.All(x => x.StartsWith("https://", StringComparison.OrdinalIgnoreCase)),
                "Cors:AllowedOrigins must be https outside Development.")
            .ValidateOnStart();

        // Resolved when CORS first needs its options, not captured at startup, so configuration
        // added after the host is built - as WebApplicationFactory does in tests - is seen.
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<WebClientCorsOptions>>((cors, configured) =>
            {
                var options = configured.Value;
                var origins = options.AllowedOrigins
                    .Select(x => TryNormalize(x, out var origin) ? origin : null)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                cors.AddPolicy(PolicyName, policy => policy
                    .SetIsOriginAllowed(origin =>
                        origins.Contains(origin) || (options.AllowLocalhostOrigins && IsLocalhost(origin)))
                    .WithMethods(Methods)
                    .WithHeaders(Headers)
                    .SetPreflightMaxAge(PreflightMaxAge));
            });

        return services;
    }

    /// <summary>
    /// The origin exactly as a browser sends it in the <c>Origin</c> header: lower-case scheme and
    /// host, the port only when it is not the default. A path, even "/", would never match a real
    /// request, so it is refused rather than silently ignored.
    /// </summary>
    private static bool TryNormalize(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*')) return false;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || value.Trim().TrimEnd('/').Length != value.Trim().Length
            || uri.AbsolutePath != "/")
            return false;

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool IsLocalhost(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.IsLoopback
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0;
}
