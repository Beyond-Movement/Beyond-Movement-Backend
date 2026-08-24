using System.Security.Claims;
using BeyondMovement.Api.Attendance;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Attendance — Phase 6. Marking a session attended is the only thing in the product that
/// consumes value the athlete paid for, so every endpoint here is Admin-only and the one that
/// deducts is never queued or retried blindly by the client (architecture section 9: it is
/// online-only, and the button is disabled offline).
/// </summary>
public static class AttendanceEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Attendance");

        sessions.MapPost("/{id:guid}/attend", Attend)
            .RequireAuthorization("AdminOnly")
            .WithName("MarkSessionAttendance")
            .WithSummary("Mark a session attended or a no-show, deducting once.")
            .WithDescription(
                "The only endpoint that moves a package balance. outcome is Attended or NoShow " +
                "and defaults to Attended; Cancelled is not accepted here because cancelling also " +
                "has to reach Calendly and has its own endpoint. " +
                "How much it deducts is decided server-side and returned as consumedSessionCount: " +
                "one for an ordinary attended session (BR-05), one for an attended observation " +
                "only if it ran longer than an hour (BR-07), and for a no-show whatever this " +
                "deployment's policy says - which is nothing by default. " +
                "The response carries the session and the package as they now stand, both changed " +
                "in one transaction, so the client can replace its copies without re-reading and " +
                "without ever showing a balance that did not exist. package and progress are null " +
                "when the session consumed nothing and the athlete has no active package. " +
                "Marking a session that is already Attended is 409 SESSION_ALREADY_ATTENDED, " +
                "already a no-show is 409 SESSION_ALREADY_RESOLVED, and cancelled is 409 " +
                "SESSION_CANCELLED (BR-06). Those are not failures to retry past - they mean the " +
                "deduction has already happened exactly once, or must never happen. A session " +
                "that would deduct from an athlete with no active package is 409 " +
                "ACTIVE_PACKAGE_NOT_FOUND, and one whose package is exhausted is 409 " +
                "NO_SESSIONS_REMAINING. Two requests racing produce one success and one 409 " +
                "CONCURRENCY_CONFLICT, never two deductions.")
            .Produces<AttendanceResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        sessions.MapGet("/{id:guid}/package-progress", Progress)
            .WithName("GetSessionPackageProgress")
            .WithSummary("Where a session sits in the athlete's package - \"Session 7 of 12\".")
            .WithDescription(
                "Separate from GET /sessions/{id} rather than added to it, so the Phase 5 session " +
                "shape the app already reads does not change. " +
                "sessionNumber is the session's own position once it has been attended, and the " +
                "position it would take if it has not been resolved yet, which is what Session " +
                "Details shows before the coach taps Mark as Attended. It is null when no " +
                "position exists to state - a cancelled session, or a short observation that will " +
                "never consume one. " +
                "404 SESSION_NOT_FOUND covers an unknown session and someone else's alike; an " +
                "athlete with no package at all gets 404 PACKAGE_NOT_FOUND, which is a normal " +
                "state and not an error worth surfacing as one.")
            .Produces<SessionPackageProgress>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        sessions.MapPost("/observations", CreateObservation)
            .RequireAuthorization("AdminOnly")
            .WithName("CreateObservation")
            .WithSummary("Record an observation the coach carried out.")
            .WithDescription(
                "The one kind of session this API creates itself. Observations are arranged in " +
                "person and never appear on a Calendly booking page, so the coach records one " +
                "after the fact; everything else is projected from Calendly and cannot be created " +
                "here. The session is created Scheduled and deducts nothing yet - a booking never " +
                "deducts (BR-04) - and is then marked attended like any other, at which point it " +
                "consumes one session only if it ran longer than an hour (BR-07). " +
                "startUtc and endUtc must both be UTC and in order, and may not span more than a " +
                "day. athleteProfileId is the profile id carried on every session, not the " +
                "athlete's user id.")
            .Produces<SessionResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        return app;
    }

    private static async Task<IResult> Attend(
        Guid id, MarkAttendanceRequest request, AttendanceService service,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        // Validated inline rather than with a validator: the only rule is that the outcome is one
        // of two enum values, and an unmapped value has to be rejected before it reaches the
        // domain, which treats it as a caller bug and throws.
        if (request.Outcome is not (SessionStatus.Attended or SessionStatus.NoShow))
            return OutcomeInvalid.ToProblem(http);

        if (!principal.TryGetIdentity(out var actorUserId, out var coachId)) return Results.Unauthorized();

        var result = await service.ResolveAsync(coachId, actorUserId, id, request.Outcome, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Progress(
        Guid id, AttendanceService service, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var query = db.Sessions.AsNoTracking().Where(x => x.Id == id && x.CoachId == coachId);

        // An athlete may read their own session's progress and nothing else. Same shape as the
        // ownership check in SchedulingEndpoints, and the same 404 rather than 403.
        if (principal.IsInRole(nameof(UserRole.Athlete)))
            query = query.Where(x => db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId));

        var session = await query.SingleOrDefaultAsync(ct);

        if (session is null) return SchedulingErrors.SessionNotFound.ToProblem(http);

        var progress = await service.ProgressAsync(session, package: null, ct);

        return progress is null
            ? Modules.Packages.PackageErrors.PackageNotFound.ToProblem(http)
            : Results.Ok(progress);
    }

    private static async Task<IResult> CreateObservation(
        CreateObservationRequest request, IValidator<CreateObservationRequest> validator,
        AppDbContext db, IClock clock, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var athlete = await (from profile in db.AthleteProfiles
                             join user in db.Users on profile.UserId equals user.Id
                             where profile.Id == request.AthleteProfileId
                                   && profile.CoachId == coachId
                                   && profile.DeletedAtUtc == null
                             select user.FullName ?? user.Email).SingleOrDefaultAsync(ct);

        if (athlete is null) return PricingErrors.AthleteNotFound.ToProblem(http);

        var session = Session.CreateObservation(coachId, request.AthleteProfileId,
            request.StartUtc, request.EndUtc, request.LocationOrPlatform, clock.UtcNow);

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/sessions/{session.Id}", session.ToResponse(athlete));
    }

    private static readonly Error OutcomeInvalid = new(ApiErrorCodes.ValidationFailed,
        "outcome must be Attended or NoShow.", StatusCodes.Status400BadRequest);
}
