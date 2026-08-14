using System.Security.Claims;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.Register;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

public static class RegistrationEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        MapRegister(app);
        MapCompleteProfile(app);
        return app;
    }

    private static void MapRegister(IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/auth/register", async (
            RegisterRequest request,
            IValidator<RegisterRequest> validator,
            RegisterHandler registerHandler,
            CreateProfileHandler profileHandler,
            AppDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            // "A valid invitation creates exactly one athlete account." The user, the athlete
            // profile and the invitation's redemption either all land or none of them do.
            // The orchestration lives here because modules must not reference each other, and
            // both handlers share this scoped DbContext, so they share this transaction.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var result = await registerHandler.HandleAsync(request, ct);

            if (result.IsFailure)
            {
                await transaction.RollbackAsync(ct);
                return result.Error!.ToProblem(http);
            }

            await profileHandler.HandleAsync(result.Value.UserId, result.Value.CoachId, ct);

            await transaction.CommitAsync(ct);

            return Results.Ok(result.Value.Auth);
        })
        .AllowAnonymous()
        .WithTags("Authentication")
        .WithName("Register")
        .WithSummary("Create an account from a validated invitation, and redeem it.")
        .WithDescription(
            "Post the registrationToken from /invitations/validate together with EITHER a password " +
            "(plus fullName) OR a googleIdToken — exactly one, never both. With Google, the " +
            "account's verified email must match the invited address or the request is refused " +
            "with GOOGLE_EMAIL_MISMATCH, and no name or password is needed. termsAccepted must be " +
            "true. Returns the same token pair as login, so the app is signed in immediately, but " +
            "profileCompleted is false: route to Complete Profile, not Home. The invitation is " +
            "redeemed only on success, and re-posting the same token afterwards returns " +
            "INVITATION_USED.")
        .Produces<AuthResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

    private static void MapCompleteProfile(IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/athletes/me/profile", async (
            CompleteProfileRequest request,
            IValidator<CompleteProfileRequest> validator,
            CompleteProfileHandler profileHandler,
            IIdentityDbContext identityDb,
            AppDbContext db,
            IClock clock,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var user = await identityDb.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user is null)
                return Results.Unauthorized();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // The name lives on the user; the athlete details live on the profile. Both move
            // together, so a half-finished profile cannot be observed.
            var result = await profileHandler.HandleAsync(
                userId, request.DateOfBirth, request.Gender, request.Sport, ct);

            if (result.IsFailure)
            {
                await transaction.RollbackAsync(ct);
                return result.Error!.ToProblem(http);
            }

            user.SetFullName(request.FullName, clock.UtcNow);
            user.MarkProfileCompleted(clock.UtcNow);
            await identityDb.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);

            var profile = await profileHandler.GetAsync(userId, ct);

            return Results.Ok(new AthleteProfileResponse(
                user.Id, user.FullName, user.Email,
                profile?.DateOfBirth, profile?.Gender, profile?.Sport,
                ProfileCompleted: true));
        })
        .RequireAuthorization("AthleteOnly")
        .WithTags("Athletes")
        .WithName("CompleteAthleteProfile")
        .WithSummary("Fill in the athlete's own profile after registration.")
        .WithDescription(
            "Athlete-only, and always scoped to the caller's own profile — the user id comes from " +
            "the token, never the body. Sets profileCompleted to true, after which /auth/me routes " +
            "the app to Home instead of Complete Profile. Safe to call again to edit the details. " +
            "Profile photo is not accepted yet; it needs file storage, which arrives in phase 13.")
        .Produces<AthleteProfileResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);
}
