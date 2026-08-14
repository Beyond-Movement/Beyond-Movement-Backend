using System.Security.Claims;
using BeyondMovement.Modules.Identity;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Features.Invitations;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

public static class InvitationEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invitations").WithTags("Invitations");

        MapValidate(group);

        // Everything else is the coach's own management screen.
        var admin = group.MapGroup(string.Empty).RequireAuthorization("AdminOnly");

        MapCreate(admin);
        MapList(admin);
        MapResend(admin);
        MapRevoke(admin);

        return app;
    }

    private static void MapValidate(RouteGroupBuilder group) =>
        group.MapGet("/validate", async (
            string code,
            ValidateInvitationHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(code))
                return IdentityErrors.InvitationInvalid.ToProblem(http);

            var result = await handler.HandleAsync(code, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.InvitationValidation)
        .WithName("ValidateInvitation")
        .WithSummary("Check an emailed invitation code and start account creation.")
        .WithDescription(
            "Does NOT consume the invitation — it is redeemed only when the account is created, " +
            "so a user may validate, abandon Create Account, and come back. Returns the invited " +
            "email (show it read-only on Create Account; it is already verified, because only " +
            "that inbox received the code) and a short-lived registrationToken to post to " +
            "/auth/register. Failures distinguish INVITATION_INVALID, INVITATION_EXPIRED, " +
            "INVITATION_USED and INVITATION_REVOKED so the error screen can say something useful. " +
            "Rate-limited per IP; exceeding it returns 429 TOO_MANY_REQUESTS.")
        .Produces<ValidateInvitationResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemJson);

    private static void MapCreate(RouteGroupBuilder group) =>
        group.MapPost(string.Empty, async (
            CreateInvitationRequest request,
            IValidator<CreateInvitationRequest> validator,
            CreateInvitationHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out var userId, out var coachId))
                return Results.Unauthorized();

            var result = await handler.HandleAsync(userId, coachId, request, ct);

            return result.IsSuccess
                ? Results.Created($"/api/v1/invitations/{result.Value.Id}", result.Value)
                : result.Error!.ToProblem(http);
        })
        .WithName("CreateInvitation")
        .WithSummary("Invite an athlete by email address.")
        .WithDescription(
            "The backend generates the code and emails it directly to the address — the response " +
            "never contains it, so the Admin cannot pass it on by another route. Re-inviting an " +
            "address that already has a pending invitation replaces the old code rather than " +
            "creating a second one. Returns 409 EMAIL_ALREADY_REGISTERED if an account exists.")
        .Produces<InvitationResponse>(StatusCodes.Status201Created)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

    private static void MapList(RouteGroupBuilder group) =>
        group.MapGet(string.Empty, async (
            ManageInvitationsHandler handler,
            ClaimsPrincipal principal,
            InvitationStatus? status,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            return Results.Ok(await handler.ListAsync(coachId, status, ct));
        })
        .WithName("ListInvitations")
        .WithSummary("List invitations, newest first, optionally filtered by status.")
        .Produces<IReadOnlyList<InvitationResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapResend(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/resend", async (
            Guid id,
            ManageInvitationsHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out var userId, out var coachId))
                return Results.Unauthorized();

            var result = await handler.ResendAsync(coachId, id, userId, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("ResendInvitation")
        .WithSummary("Issue a fresh code and email it again.")
        .WithDescription("The previous code stops working immediately, so only one code is ever live.")
        .Produces<InvitationResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapRevoke(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", async (
            Guid id,
            ManageInvitationsHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out var userId, out var coachId))
                return Results.Unauthorized();

            var result = await handler.RevokeAsync(coachId, id, userId, ct);

            return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem(http);
        })
        .WithName("RevokeInvitation")
        .WithSummary("Cancel a pending invitation.")
        .WithDescription("An already-redeemed invitation cannot be revoked — deleting the account is a separate action.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);
}
