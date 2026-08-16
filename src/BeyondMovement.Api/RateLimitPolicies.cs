using System.Threading.RateLimiting;
using BeyondMovement.Modules.Identity.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace BeyondMovement.Api;

public static class RateLimitPolicies
{
    /// <summary>
    /// Invitation codes are short enough to type, so an unthrottled validate endpoint is a
    /// guessing oracle. Architecture section 12.5 requires this limit.
    /// </summary>
    public const string InvitationValidation = "invitation-validation";

    /// <summary>
    /// Ten per hour per IP, per architecture section 12.5 — the changelog's "per minute" was a
    /// drift in the implementation, and the architecture is the source of truth (CLAUDE.md
    /// section 1). Ten attempts is far more than an athlete typing one code off an email needs,
    /// and an hour is long enough that guessing is hopeless.
    /// </summary>
    private const int DefaultPermitLimit = 10;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(InvitationValidation, context =>
            {
                // Resolved per request, not captured at startup: configuration sources added
                // after the host is built - as WebApplicationFactory does in tests - are
                // invisible to anything read from builder.Configuration during registration.
                var permitLimit = context.RequestServices
                    .GetRequiredService<IConfiguration>()
                    .GetValue("RateLimits:InvitationValidationPerHour", DefaultPermitLimit);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = Window
                    });
            });

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                // The limiter reports how long the window has left. The fallback is the whole
                // window, which over-waits rather than inviting an immediate retry that fails.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? (int)value.TotalSeconds
                    : (int)Window.TotalSeconds;

                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();

                await context.HttpContext.Response.WriteAsJsonAsync(new ApiProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc6585#section-4",
                    Title = "Too many attempts. Wait a moment and try again.",
                    Status = StatusCodes.Status429TooManyRequests,
                    ErrorCode = ApiErrorCodes.TooManyRequests,
                    CorrelationId = context.HttpContext.TraceIdentifier,
                    RetryAfterSeconds = retryAfter
                }, ct);
            };
        });
}
