using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Middleware;

/// <summary>
/// BR-10. An access token stays valid for its full 15 minutes after an account is paused,
/// so the token alone is not enough — the current status is checked on every authenticated
/// request. Phase 9 puts a 60-second Redis cache in front of this lookup; one database
/// round trip per request is acceptable until then.
/// </summary>
public sealed class PausedAccountMiddleware(RequestDelegate next, ILogger<PausedAccountMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IIdentityDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var subject = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(subject, out var userId))
        {
            // Authenticated but the token carries no usable subject — treat as unauthenticated.
            logger.LogWarning("Authenticated request carried an unparsable subject claim");
            await Deny(context, ApiErrorCodes.InvalidToken, StatusCodes.Status401Unauthorized);
            return;
        }

        var status = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserStatus?)u.Status)
            .FirstOrDefaultAsync(context.RequestAborted);

        if (status is null)
        {
            await Deny(context, ApiErrorCodes.InvalidToken, StatusCodes.Status401Unauthorized);
            return;
        }

        if (status != UserStatus.Active)
        {
            await Deny(context, ApiErrorCodes.AccountPaused, StatusCodes.Status403Forbidden);
            return;
        }

        await next(context);
    }

    // Same envelope as every other error, so the client has exactly one shape to parse.
    private static async Task Deny(HttpContext context, string errorCode, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ApiProblemDetails
        {
            Type = statusCode == StatusCodes.Status403Forbidden
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.4"
                : "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            Title = errorCode == ApiErrorCodes.AccountPaused
                ? "This account is paused."
                : "The token is no longer valid.",
            Status = statusCode,
            ErrorCode = errorCode,
            CorrelationId = context.TraceIdentifier
        });
    }
}
