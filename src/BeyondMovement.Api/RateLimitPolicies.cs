using System.Threading.RateLimiting;
using BeyondMovement.Modules.Identity.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace BeyondMovement.Api;

public static class RateLimitPolicies
{
    /// <summary>
    /// Invitation codes are short enough to type, so an unthrottled validate endpoint is a
    /// guessing oracle. Architecture section 7.1 requires this limit.
    /// </summary>
    public const string InvitationValidation = "invitation-validation";

    private const int DefaultPermitLimit = 10;

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
                    .GetValue("RateLimits:InvitationValidationPerMinute", DefaultPermitLimit);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1)
                    });
            });

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? (int)value.TotalSeconds
                    : 60;

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
