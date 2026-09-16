using System.Security.Claims;
using BeyondMovement.Api.Scheduling;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Observation Requests — the athlete asks to be observed, and the Admin answers.
/// <para>
/// This is the second way an Observation session comes to exist. The first, A-03's
/// <c>POST /sessions/observations</c>, is unchanged and still the Admin recording one directly;
/// this one starts with the athlete. Both end in the same place — a <c>Session</c> with
/// <c>deliveryType: Observation</c> — and everything afterwards, attendance, notes,
/// cancellation, package progress, is the behaviour that session already has.
/// </para>
/// <para>
/// <b>A request is not a session.</b> Nothing appears on the schedule and nothing can be
/// attended until the Admin accepts. Nothing is deducted then either: a booking never deducts
/// (BR-04), and the deduction choice the Admin makes at acceptance is applied later at Mark as
/// Attended (BR-07).
/// </para>
/// </summary>
public static class ObservationRequestEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapObservationRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/v1/me/observation-requests")
            .WithTags("Observation requests")
            .RequireAuthorization("AthleteOnly");

        mine.MapPost(string.Empty, Create)
            .WithName("CreateObservationRequest")
            .WithSummary("Ask the coach to observe a competition or training session.")
            .WithDescription(
                "Always the caller's own - there is no athlete id in the route or the body, so a " +
                "request cannot be filed against anyone else. Starts Pending: nothing appears on " +
                "the schedule, nothing can be attended, and no package session is touched. " +
                "requestedStartUtc must be UTC and IN THE FUTURE - convert the local date and " +
                "time the athlete picked before sending, the same way booking does. location is " +
                "REQUIRED: an athlete asking to be observed is asking their coach to come " +
                "somewhere. details is OPTIONAL free text for the coach - what is being trained " +
                "or competed in, and what they would like watched. " +
                "requestedDurationMinutes is optional and defaults to 60; the athlete's form " +
                "does not collect it, and the Admin can change it when they accept. " +
                "An athlete may have SEVERAL pending requests at once - two competitions are two " +
                "things to ask for - which is deliberately unlike a package purchase, where only " +
                "one may be pending. " +
                "REQUIRES AN ACTIVE PACKAGE THAT INCLUDES OBSERVATIONS. The athlete's active " +
                "purchased package must carry the Observations feature code in its " +
                "includedFeatures, or this is 403 OBSERVATIONS_NOT_INCLUDED - which is also the " +
                "answer when they have no active package at all. Read includedFeatures from " +
                "GET /me/package to show or hide the action; that is UX only, and this endpoint " +
                "enforces the rule whatever the app drew. Eligibility is decided from the " +
                "SNAPSHOT taken when the package was bought, never from the catalogue option as " +
                "it stands today, and never from a feature's display text. " +
                "NO SESSION IS CONSUMED by asking: the balance moves at Mark as Attended and " +
                "nowhere else (BR-04).")
            .Produces<ObservationRequestResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        mine.MapGet(string.Empty, ListMine)
            .WithName("ListMyObservationRequests")
            .WithSummary("A page of the calling athlete's own observation requests.")
            .WithDescription(
                "Always the caller's own, and an athlete can never read another's. Omit status " +
                "to see every request; pass status=Pending for the ones still waiting on the " +
                "coach. " +
                "PAGED, in the same envelope as every other list in this API: items plus page, " +
                "pageSize, totalCount, totalPages, hasNextPage and hasPreviousPage. page starts " +
                "at 1 and pageSize defaults to 20 and is capped at 100 - values outside the " +
                "range are clamped rather than rejected. " +
                "Ordered by requestedStartUtc, soonest first, with the id breaking ties so a " +
                "request cannot appear on two pages. " +
                "An athlete who has never asked for one gets an EMPTY PAGE, not a 404.")
            .Produces<PagedResult<ObservationRequestResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        mine.MapGet("/{id:guid}", GetMine)
            .WithName("GetMyObservationRequest")
            .WithSummary("One of the calling athlete's own observation requests.")
            .WithDescription(
                "The same object the list returns, so one model serves both. A request that does " +
                "not exist and one belonging to another athlete are both 404 " +
                "OBSERVATION_REQUEST_NOT_FOUND - deliberately indistinguishable, so an id cannot " +
                "be probed for existence. " +
                "Once status is Accepted, sessionId names the observation session that was " +
                "created; read GET /sessions/{sessionId} for it, and use the ordinary session " +
                "endpoints for anything that happens afterwards.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        mine.MapPut("/{id:guid}", ReviseMine)
            .WithName("UpdateMyObservationRequest")
            .WithSummary("Change a request the coach has not answered yet.")
            .WithDescription(
                "PENDING ONLY. Once the coach has accepted, declined, or the athlete has " +
                "cancelled, the request is READ-ONLY and this is 409 " +
                "OBSERVATION_REQUEST_NOT_PENDING. After an acceptance the date and time belong " +
                "to the session that was created, not to this request - change them there, " +
                "through the ordinary session behaviour. " +
                "A FULL REPLACEMENT, not a patch: send requestedStartUtc, location, details and " +
                "requestedDurationMinutes every time. OMITTING details CLEARS IT, and an omitted " +
                "requestedDurationMinutes returns the request to the 60-minute default. " +
                "The same rules the create applies: UTC, in the future, location required.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        mine.MapPost("/{id:guid}/cancel", CancelMine)
            .WithName("CancelMyObservationRequest")
            .WithSummary("Withdraw a request the coach has not answered yet.")
            .WithDescription(
                "PENDING ONLY, and 409 OBSERVATION_REQUEST_NOT_PENDING otherwise - including on " +
                "a repeat, so a double tap is a conflict rather than a silent success. " +
                "NO SESSION IS CREATED and none ever will be for this request; the athlete files " +
                "a new one if they change their mind again. " +
                "A request the coach has ALREADY ACCEPTED cannot be cancelled here - by then " +
                "there is a real session, and cancelling that is POST /sessions/{id}/cancel.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        var admin = app.MapGroup("/api/v1/observation-requests")
            .WithTags("Observation requests")
            .RequireAuthorization("AdminOnly");

        admin.MapGet(string.Empty, List)
            .WithName("ListObservationRequests")
            .WithSummary("A page of observation requests, soonest first, filterable.")
            .WithDescription(
                "The coach's review queue. Omit status to see every request; pass " +
                "status=Pending for the ones still waiting to be answered. athleteId is the " +
                "athlete's USER id, matching every other /athletes/{athleteId} route - an " +
                "unknown one is 404 ATHLETE_NOT_FOUND rather than an empty list, because an " +
                "empty list is a real answer and a bad id must not be mistaken for one. " +
                "PAGED in the usual envelope, page from 1, pageSize 20 by default and capped at " +
                "100, clamped rather than rejected. Filters apply BEFORE paging, so totalCount " +
                "is the number matching the filter. " +
                "Ordered by requestedStartUtc, SOONEST FIRST - a queue is a list of things about " +
                "to happen, and the one with least time left belongs at the top - with the id " +
                "breaking ties. " +
                "Every row carries athleteName and athleteUserId, so the queue needs no second " +
                "call to label itself.")
            .Produces<PagedResult<ObservationRequestResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        admin.MapGet("/{id:guid}", Detail)
            .WithName("GetObservationRequest")
            .WithSummary("One observation request.")
            .WithDescription(
                "A request belonging to another coach is 404 OBSERVATION_REQUEST_NOT_FOUND, the " +
                "same as one that does not exist, so an id cannot be probed for existence.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        admin.MapPut("/{id:guid}", ReviseAsAdmin)
            .WithName("UpdateObservationRequest")
            .WithSummary("Adjust what the athlete proposed, without answering it yet.")
            .WithDescription(
                "PENDING ONLY, and 409 OBSERVATION_REQUEST_NOT_PENDING otherwise. For the coach " +
                "who wants to move the time or correct the place and leave the request waiting - " +
                "the athlete sees the adjusted proposal and the request stays Pending. " +
                "The SAME BODY AND THE SAME RULES as the athlete's own PUT: a full replacement, " +
                "so send every field every time, omitting details CLEARS it, and " +
                "requestedStartUtc must be UTC and in the future. " +
                "To adjust AND accept in one step, send the same fields to /accept instead - " +
                "that applies them and creates the session in one transaction, which is the " +
                "better call when the coach has already decided.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        admin.MapPost("/{id:guid}/accept", Accept)
            .WithName("AcceptObservationRequest")
            .WithSummary("Accept a request, creating the observation session.")
            .WithDescription(
                "PENDING ONLY. In ONE TRANSACTION this records the decision, applies any changes " +
                "the Admin made, creates a Session with deliveryType Observation, and links the " +
                "two - so an accepted request without its session cannot exist. The response " +
                "carries both as they now stand, so the app can replace its copy of the request " +
                "and add the session without re-reading either. " +
                "deductSession is REQUIRED and is the Admin's explicit BR-07 choice about " +
                "whether attending this observation will consume one package session. The " +
                "athlete never had that choice to make, which is why it is not on the request " +
                "and must be answered here. NOTHING IS DEDUCTED NOW: the session is created " +
                "Scheduled, a booking never deducts (BR-04), and the choice is stored and " +
                "applied at Mark as Attended. NO ACTIVE PACKAGE IS REQUIRED to accept - as with " +
                "POST /sessions/observations, the balance is only checked when attendance is " +
                "recorded. " +
                "requestedStartUtc, location, details and requestedDurationMinutes are OVERRIDES: " +
                "send one to change what the athlete asked for, omit it to accept as requested. " +
                "Whatever is accepted is written back onto the request too, so it records what " +
                "was agreed rather than what was originally proposed. " +
                "Deliberately NO future-date rule: a request that waited until its date passed " +
                "may still be accepted, exactly as POST /sessions/observations permits a past " +
                "observation. " +
                "REPEATING IT IS 409 OBSERVATION_REQUEST_NOT_PENDING, never a second session. " +
                "Note this is stricter than POST /purchases/{id}/mark-paid, which answers a " +
                "repeat with the package it already made: an accept that timed out must RE-READ " +
                "the request to find its sessionId rather than being retried. " +
                "This endpoint does not touch Calendly. Observations are arranged in person and " +
                "never appear on a booking page (A-03), so the session it creates is local only " +
                "and has no meetingUrl or rescheduleUrl.")
            .Produces<AcceptObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        admin.MapPost("/{id:guid}/decline", Decline)
            .WithName("DeclineObservationRequest")
            .WithSummary("Decline a request. No session is created.")
            .WithDescription(
                "PENDING ONLY, and 409 OBSERVATION_REQUEST_NOT_PENDING otherwise - including on " +
                "a repeat. NO SESSION IS CREATED and none ever will be for this request; the " +
                "request becomes read-only and the athlete files a new one if they want to " +
                "propose another time. " +
                "There is no decline reason field. If the coach needs to explain, that is a " +
                "conversation, not a column.")
            .Produces<ObservationRequestResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        return app;
    }

    // ---------------------------------------------------------------- athlete

    private static async Task<IResult> Create(
        SaveObservationRequestRequest request, IValidator<SaveObservationRequestRequest> validator,
        ObservationRequestService service, ObservationRequestReader reader,
        ObservationEligibility eligibility, AppDbContext db,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var athleteProfileId = await OwnProfileIdAsync(db, userId, coachId, ct);
        if (athleteProfileId is null) return PricingErrors.AthleteNotFound.ToProblem(http);

        // The package gate, and the only endpoint in this file that has one. Checked after
        // ownership so an athlete with no profile still gets ATHLETE_NOT_FOUND rather than being
        // told about a package they could not have, and before anything is written so a refused
        // request leaves nothing behind.
        var mayRequest = await eligibility.MayRequestAsync(athleteProfileId.Value, ct);
        if (mayRequest.IsFailure) return mayRequest.Error!.ToProblem(http);

        var created = await service.CreateAsync(coachId, athleteProfileId.Value, request, ct);
        var response = await reader.LabelAsync(created, ct);

        return Results.Created($"/api/v1/me/observation-requests/{created.Id}", response);
    }

    private static async Task<IResult> ListMine(
        ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        CancellationToken ct,
        ObservationRequestStatus? status = null,
        int page = 1, int pageSize = PagedResult<ObservationRequestResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var athleteProfileId = await OwnProfileIdAsync(db, userId, coachId, ct);
        var (normalizedPage, normalizedSize) =
            PagedResult<ObservationRequestResponse>.Normalize(page, pageSize);

        // An athlete with no profile has no requests, which is an empty page rather than an
        // error - the same answer GET /sessions gives them.
        if (athleteProfileId is null)
            return Results.Ok(new PagedResult<ObservationRequestResponse>(
                [], normalizedPage, normalizedSize, 0));

        return Results.Ok(await reader.ListAsync(
            coachId, status, athleteProfileId, normalizedPage, normalizedSize, ct));
    }

    private static async Task<IResult> GetMine(
        Guid id, ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var athleteProfileId = await OwnProfileIdAsync(db, userId, coachId, ct);
        if (athleteProfileId is null) return SchedulingErrors.ObservationRequestNotFound.ToProblem(http);

        var response = await reader.GetAsync(coachId, id, athleteProfileId, ct);

        return response is null
            ? SchedulingErrors.ObservationRequestNotFound.ToProblem(http)
            : Results.Ok(response);
    }

    private static async Task<IResult> ReviseMine(
        Guid id, SaveObservationRequestRequest request,
        IValidator<SaveObservationRequestRequest> validator, ObservationRequestService service,
        ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        var owned = await OwnRequestAsync(id, db, principal, ct);
        if (owned is null) return SchedulingErrors.ObservationRequestNotFound.ToProblem(http);

        var revised = await service.ReviseAsync(owned, request, ct);

        return revised.IsSuccess
            ? Results.Ok(await reader.LabelAsync(owned, ct))
            : revised.Error!.ToProblem(http);
    }

    private static async Task<IResult> CancelMine(
        Guid id, ObservationRequestService service, ObservationRequestReader reader,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var owned = await OwnRequestAsync(id, db, principal, ct);
        if (owned is null) return SchedulingErrors.ObservationRequestNotFound.ToProblem(http);

        if (!principal.TryGetUserId(out var actorUserId)) return Results.Unauthorized();

        var cancelled = await service.ResolveAsync(
            owned, ObservationRequestDecision.Cancelled, actorUserId, ct);

        return cancelled.IsSuccess
            ? Results.Ok(await reader.LabelAsync(owned, ct))
            : cancelled.Error!.ToProblem(http);
    }

    // ------------------------------------------------------------------ admin

    private static async Task<IResult> List(
        ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct,
        ObservationRequestStatus? status = null, Guid? athleteId = null,
        int page = 1, int pageSize = PagedResult<ObservationRequestResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        Guid? athleteProfileId = null;

        if (athleteId is { } userId)
        {
            // The route takes the athlete's user id, like every other /athletes route, while
            // requests are keyed by profile id. An unknown id is 404 rather than an empty page:
            // an empty page is a real answer here and a bad id must not be mistaken for one.
            athleteProfileId = await db.AthleteProfiles.AsNoTracking()
                .Where(x => x.UserId == userId && x.CoachId == coachId && x.DeletedAtUtc == null)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(ct);

            if (athleteProfileId is null) return PricingErrors.AthleteNotFound.ToProblem(http);
        }

        var (normalizedPage, normalizedSize) =
            PagedResult<ObservationRequestResponse>.Normalize(page, pageSize);

        return Results.Ok(await reader.ListAsync(
            coachId, status, athleteProfileId, normalizedPage, normalizedSize, ct));
    }

    private static async Task<IResult> Detail(
        Guid id, ObservationRequestReader reader, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var response = await reader.GetAsync(coachId, id, athleteProfileId: null, ct);

        return response is null
            ? SchedulingErrors.ObservationRequestNotFound.ToProblem(http)
            : Results.Ok(response);
    }

    private static async Task<IResult> ReviseAsAdmin(
        Guid id, SaveObservationRequestRequest request,
        IValidator<SaveObservationRequestRequest> validator, ObservationRequestService service,
        ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var found = await db.ObservationRequests
            .SingleOrDefaultAsync(x => x.Id == id && x.CoachId == coachId, ct);

        if (found is null) return SchedulingErrors.ObservationRequestNotFound.ToProblem(http);

        var revised = await service.ReviseAsync(found, request, ct);

        return revised.IsSuccess
            ? Results.Ok(await reader.LabelAsync(found, ct))
            : revised.Error!.ToProblem(http);
    }

    private static async Task<IResult> Accept(
        Guid id, AcceptObservationRequestRequest request,
        IValidator<AcceptObservationRequestRequest> validator, ObservationRequestService service,
        ObservationRequestReader reader, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var actorUserId, out var coachId)) return Results.Unauthorized();

        var result = await service.AcceptAsync(coachId, id, actorUserId, request, ct);

        if (result.IsFailure) return result.Error!.ToProblem(http);

        var labelled = await reader.LabelAsync(result.Value.Request, ct);

        return Results.Ok(new AcceptObservationRequestResponse(
            labelled, result.Value.Session.ToResponse(labelled.AthleteName)));
    }

    private static async Task<IResult> Decline(
        Guid id, ObservationRequestService service, ObservationRequestReader reader,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var actorUserId, out var coachId)) return Results.Unauthorized();

        var found = await db.ObservationRequests
            .SingleOrDefaultAsync(x => x.Id == id && x.CoachId == coachId, ct);

        if (found is null) return SchedulingErrors.ObservationRequestNotFound.ToProblem(http);

        var declined = await service.ResolveAsync(
            found, ObservationRequestDecision.Declined, actorUserId, ct);

        return declined.IsSuccess
            ? Results.Ok(await reader.LabelAsync(found, ct))
            : declined.Error!.ToProblem(http);
    }

    // ----------------------------------------------------------------- shared

    /// <summary>
    /// The calling athlete's own profile id. Requests are keyed by it and no endpoint returns
    /// it, so it is resolved from the token the same way <c>SchedulingEndpoints.Book</c> does.
    /// </summary>
    private static Task<Guid?> OwnProfileIdAsync(
        AppDbContext db, Guid userId, Guid coachId, CancellationToken ct) =>
        db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == userId && x.CoachId == coachId && x.DeletedAtUtc == null)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// One of the caller's own requests, tracked so it can be written. Another athlete's request
    /// and one that does not exist both come back null, so both answer 404 - the ownership check
    /// is part of the query rather than a test on a row that was already loaded.
    /// </summary>
    private static async Task<ObservationRequest?> OwnRequestAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return null;

        return await db.ObservationRequests
            .Where(x => x.Id == id && x.CoachId == coachId
                        && db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId))
            .SingleOrDefaultAsync(ct);
    }
}
