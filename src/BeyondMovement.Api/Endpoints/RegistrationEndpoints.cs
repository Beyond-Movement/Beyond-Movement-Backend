using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.Register;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

public static class RegistrationEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        MapRegister(app);
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
            "Establishes authentication and nothing else — it does not collect a name. Post the " +
            "registrationToken from /invitations/validate together with EITHER a password OR a " +
            "googleIdToken — exactly one, never both. With Google, the account's verified email " +
            "must match the invited address or the request is refused with GOOGLE_EMAIL_MISMATCH. " +
            "Returns the same token pair as login, so the app is " +
            "signed in immediately, but user.profileCompleted is false and user.fullName is null " +
            "(or Google's display name, as a prefill): route to Complete Profile, not Home. The " +
            "invitation is redeemed only on success, and re-posting the same token afterwards " +
            "returns INVITATION_USED.")
        .Produces<AuthResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);
}
