using System.Security.Claims;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// The athlete's own profile: the read behind the Athlete Profile screen, and the write behind
/// both Complete Profile and Edit Profile.
/// <para>
/// One route, <c>/athletes/me/profile</c>, and always the caller's own — the user id comes from
/// the token, and there is no id in the route or the body to authorise against. This is the
/// athlete's counterpart to the Admin's <c>/auth/me/profile</c>, which is a different screen
/// with different fields and stays Admin-only.
/// </para>
/// <para>
/// Both halves span two modules — the name and phone live on the Identity <c>User</c>, the
/// sport, date of birth and gender on the Athletes <c>AthleteProfile</c> — so they are
/// orchestrated here in the composition root rather than inside either module (CLAUDE.md
/// section 4). The write does both in one transaction, so a half-saved profile cannot be seen.
/// </para>
/// </summary>
public static class AthleteProfileEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapAthleteProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/athletes/me/profile")
            .WithTags("Athletes")
            .RequireAuthorization("AthleteOnly");

        MapGetMyProfile(group);
        MapSaveMyProfile(group);

        return app;
    }

    private static void MapGetMyProfile(RouteGroupBuilder group) =>
        group.MapGet(string.Empty, async (
            IIdentityDbContext identityDb,
            CompleteProfileHandler profileHandler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var user = await identityDb.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user is null || user.Status == UserStatus.Deleted)
                return Results.Unauthorized();

            // Read separately rather than joined: two single-row lookups on primary and unique
            // keys, and the write path already treats the pair this way.
            var profile = await profileHandler.GetAsync(userId, ct);

            return Results.Ok(new AthleteProfileResponse(
                user.Id, user.FullName, user.Email, user.Phone,
                profile?.DateOfBirth, profile?.Gender, profile?.Sport,
                user.ProfileCompleted));
        })
        .WithName("GetMyAthleteProfile")
        .WithSummary("The signed-in athlete's own profile.")
        .WithDescription(
            "What the Athlete Profile screen reads when it opens: fullName, email, phone, " +
            "dateOfBirth, gender and sport. Always the caller's own - there is no athlete id, " +
            "and an athlete can never read another's profile. " +
            "EMAIL IS DISPLAY-ONLY. It is returned so the screen can show it and is not accepted " +
            "by the POST; see that endpoint for why. " +
            "Every field except email and profileCompleted can be null, and an athlete who has " +
            "registered but not finished Complete Profile has all of them null at once - that is " +
            "the state profileCompleted: false describes, not an error. Once profileCompleted is " +
            "true, fullName, dateOfBirth, gender and sport are all non-null; phone stays " +
            "optional and is null until the athlete gives one. " +
            "Profile photo is not part of this response: it needs file storage, upload and " +
            "public serving, which is a phase of its own. Show initials.")
        .Produces<AthleteProfileResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapSaveMyProfile(RouteGroupBuilder group) =>
        group.MapPost(string.Empty, async (
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

            // The name and phone live on the user; the athlete details live on the profile.
            // Both move together, so a half-finished profile cannot be observed.
            var result = await profileHandler.HandleAsync(
                userId, request.DateOfBirth, request.Gender, request.Sport, ct);

            if (result.IsFailure)
            {
                await transaction.RollbackAsync(ct);
                return result.Error!.ToProblem(http);
            }

            var now = clock.UtcNow;

            // Order matters: the name has to be on the user before the profile can be marked
            // complete, because that is where the "completed implies named" invariant is kept.
            user.SetFullName(request.FullName, now);
            user.SetPhone(request.Phone, now);
            user.MarkProfileCompleted(now);
            await identityDb.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);

            // The athlete details are echoed from the request - they are what was just
            // committed, and a second round trip could only disagree. Phone is read back off
            // the entity instead, because SetPhone trims and turns a blank into null, so what
            // was stored is not always what was sent.
            return Results.Ok(new AthleteProfileResponse(
                user.Id, user.FullName, user.Email, user.Phone,
                request.DateOfBirth, request.Gender, request.Sport,
                ProfileCompleted: true));
        })
        .WithName("CompleteAthleteProfile")
        .WithSummary("Fill in, or edit, the athlete's own profile.")
        .WithDescription(
            "Athlete-only, and always scoped to the caller's own profile - the user id comes " +
            "from the token, never the body. One endpoint serves both Complete Profile and Edit " +
            "Profile: they set the same fields, and a second edit endpoint could only drift " +
            "from this one. Safe to call as often as the athlete edits. " +
            "A FULL REPLACEMENT, NOT A PATCH: send fullName, dateOfBirth, gender and sport " +
            "every time, and send phone every time you intend to keep it - a field left out of " +
            "the body is cleared, not left alone. " +
            "fullName, dateOfBirth, gender and sport are all required and enforced here, not " +
            "only in the app. phone is optional: send null or an empty string to clear it, and " +
            "it reads back as null either way. Digits and + ( ) - . only, up to 40 characters; " +
            "the format is otherwise unconstrained because numbers are international and " +
            "displayed, not dialled - the same rule the Admin's profile applies. " +
            "EMAIL CANNOT BE CHANGED HERE. It is the login identity, the unique key on the user " +
            "and what Google sign-in matches on, so changing it needs re-verification and " +
            "re-issued tokens - a feature of its own. It is absent from this request and " +
            "untouched by this call. " +
            "Sets profileCompleted to true, after which both /auth/me and every later " +
            "authentication response report it as true and guarantee a non-null fullName, so the " +
            "app routes to Home instead of Complete Profile. " +
            "The response carries phone as stored, after trimming, so render that field from the " +
            "response rather than from what was sent. " +
            "Profile photo is not accepted yet; it needs file storage, upload and public " +
            "serving, which is a phase of its own, and the app shows initials until then.")
        .Produces<AthleteProfileResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);
}
