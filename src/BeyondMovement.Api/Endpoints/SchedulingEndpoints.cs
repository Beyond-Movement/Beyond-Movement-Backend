using System.Security.Claims;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Calendly;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Features;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace BeyondMovement.Api.Endpoints;

public static class SchedulingEndpoints
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>The bound the handler enforces, and the bound the contract advertises.</summary>
    private const int MaxIdempotencyKeyLength = 100;

    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        var booking = app.MapGroup("/api/v1/scheduling").WithTags("Scheduling");

        booking.MapGet("/session-types", GetTypes)
            .RequireAuthorization("AthleteOnly")
            .WithName("GetBookableSessionTypes")
            .WithSummary("The session types the athlete may book.")
            .WithDescription(
                "Read live from Calendly on every call, and filtered twice: the event type must be " +
                "active in Calendly and mapped in this API's configuration, so a paused or unmapped " +
                "type never reaches the app. id is the Calendly event type's trailing identifier and " +
                "is what /availability and POST /bookings take - it is not a database id. locations " +
                "is what Calendly offers for that type: empty means there is no location choice to " +
                "make, one entry means it is fixed, and more than one means the athlete must choose " +
                "and send the chosen kind as locationKind when booking. Returns 503 " +
                "CALENDLY_UNAVAILABLE when Calendly is not configured or is not answering - that is " +
                "a transient failure, not an empty catalogue, and must not be shown as 'no sessions " +
                "available'.")
            .Produces<IReadOnlyList<BookableSessionType>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        booking.MapGet("/session-types/{eventTypeId}/availability", GetAvailability)
            .RequireAuthorization("AthleteOnly")
            .WithName("GetSessionTypeAvailability")
            .WithSummary("Bookable start times for one session type, in a UTC window.")
            .WithDescription(
                "fromUtc and toUtc are both required, must both carry a UTC offset (Z), must be in " +
                "the future and in order, and may span at most 7 days - anything else is 400 " +
                "AVAILABILITY_RANGE_INVALID rather than a silently clamped range. Seven is " +
                "Calendly's own limit on an availability query, not a choice made here: ask for a " +
                "month a week at a time. Each slot carries " +
                "startUtc and endUtc, the end computed from the session type's duration, so the " +
                "client never has to add the minutes itself. The list is a snapshot: a slot can be " +
                "taken between this call and the booking, which is why POST /bookings re-checks and " +
                "can still answer 409 SLOT_UNAVAILABLE. An unknown or unmapped eventTypeId is 404 " +
                "EVENT_TYPE_INVALID.")
            .Produces<IReadOnlyList<AvailableSlot>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        booking.MapPost("/bookings", Book)
            .RequireAuthorization("AthleteOnly")
            .WithName("BookSession")
            .WithSummary("Book one available slot for the calling athlete.")
            .WithDescription(
                "Always books for the caller's own athlete profile - there is no athlete id in the " +
                "body and an Admin cannot book on someone's behalf here. Idempotency-Key is required " +
                "and is remembered per athlete: replaying the same key returns the session that key " +
                "already created rather than booking a second one, and returns 409 " +
                "BOOKING_IN_PROGRESS with Retry-After while the first attempt is still in flight. " +
                "Generate the key once per booking attempt and keep it across retries. startUtc must " +
                "be UTC, in the future, and match an available slot exactly; timeZone is an IANA " +
                "name such as Africa/Cairo and is what the Calendly invitation is written in. " +
                "locationKind is required only when the session type offers a choice - 400 " +
                "LOCATION_REQUIRED and LOCATION_INVALID say which. Returns 201 with the created " +
                "session, including meetingUrl for an online session and the rescheduleUrl the app " +
                "opens later.")
            .AddOpenApiOperationTransformer(DeclareIdempotencyKey)
            .Produces<SessionResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        booking.MapPost("/refresh", RefreshScheduling)
            .RequireAuthorization("AdminOnly")
            .WithName("RefreshScheduling")
            .WithSummary("Queue a reconciliation of this API's sessions against Calendly.")
            .WithDescription(
                "An Admin-only operational escape hatch for a missed webhook, not something the " +
                "athlete app calls. Returns 202 with an empty body and no schema deliberately: the " +
                "work is queued and runs in the background, so the response cannot say what changed " +
                "and there is nothing for the client to model. Read the sessions endpoints again " +
                "afterwards to see the result.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Sessions");

        sessions.MapGet(string.Empty, List)
            .WithName("ListSessions")
            .WithSummary("The caller's sessions, earliest start first, one page at a time.")
            .WithDescription(
                "An athlete sees only their own sessions and athleteProfileId is ignored for them; " +
                "an Admin sees the coach's sessions and may narrow to one athlete with it. fromUtc " +
                "is inclusive and toUtc exclusive, both UTC. limit is clamped to 1-100, and 0 means " +
                "30. cursor carries the start time the next page resumes after - treat it as opaque " +
                "and pass nextCursor back unchanged. A null nextCursor is the last page; a non-null " +
                "one means at least one more session follows, so page until it is null rather than " +
                "until a short page. An athlete with no profile gets an empty page, not an error.")
            .Produces<SessionPage>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson);

        sessions.MapGet("/upcoming", Upcoming)
            .WithName("GetUpcomingSessions")
            .WithSummary("The caller's next scheduled sessions, for the home screen.")
            .WithDescription(
                "The same page shape as /sessions, and exactly equivalent to it with fromUtc set to " +
                "now and status set to Scheduled, so a cancelled session never appears here. limit " +
                "is clamped to 1-100 and 0 means 10. nextCursor is filled in on the same terms as " +
                "/sessions; to walk the whole list, continue with /sessions rather than paging this " +
                "endpoint.")
            .Produces<SessionPage>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson);

        sessions.MapGet("/{id:guid}", Detail)
            .WithName("GetSession")
            .WithSummary("One session, if it belongs to the caller.")
            .WithDescription(
                "The same object the list returns, so one model serves both. A session that does " +
                "not exist and a session belonging to another athlete are both 404 " +
                "SESSION_NOT_FOUND - deliberately indistinguishable, so an id cannot be probed for " +
                "existence.")
            .Produces<SessionResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        sessions.MapPost("/{id:guid}/cancel", Cancel)
            .WithName("CancelSession")
            .WithSummary("Cancel a session, in Calendly and here.")
            .WithDescription(
                "Cancels in Calendly first and only then locally, so the two cannot disagree: if " +
                "Calendly refuses, the session is left scheduled and the call is 503 with nothing " +
                "changed. Returns the updated session with status Cancelled, so the client can " +
                "replace its copy from the response without re-reading. Cancelling an " +
                "already-cancelled session succeeds and returns it unchanged, which makes a " +
                "repeated tap safe. reason is optional and is passed on to Calendly.")
            .Produces<SessionResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        sessions.MapGet("/{id:guid}/reschedule", Reschedule)
            .WithName("GetSessionRescheduleUrl")
            .WithSummary("The Calendly reschedule link for a session.")
            .WithDescription(
                "Open url in a browser or web view; rescheduling itself happens in Calendly, not " +
                "here, and reaches this API as a webhook, so re-read the session afterwards rather " +
                "than assuming the new time. 404 SESSION_NOT_FOUND covers all three of an unknown " +
                "id, someone else's session, and a session Calendly gave no reschedule link for.")
            .Produces<RescheduleUrlResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        app.MapPost("/api/v1/webhooks/calendly", ReceiveWebhook)
            .AllowAnonymous().WithTags("Calendly webhooks")
            .DisableAntiforgery();
        return app;
    }

    /// <summary>
    /// The header binds as optional so that a missing one becomes IDEMPOTENCY_KEY_REQUIRED like
    /// every other error in this API, rather than a bare framework 400 carrying no error code. The
    /// contract still has to say it is required, and carry the same length bound the handler
    /// enforces, or a client learns of either only by being rejected.
    /// </summary>
    private static Task DeclareIdempotencyKey(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (operation.Parameters?.SingleOrDefault(x => x.Name == "Idempotency-Key") is OpenApiParameter parameter)
        {
            parameter.Required = true;

            if (parameter.Schema is OpenApiSchema schema)
                schema.MaxLength = MaxIdempotencyKeyLength;
        }

        return Task.CompletedTask;
    }

    private static async Task<IResult> GetTypes(SchedulingService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.GetBookableTypesAsync(ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> GetAvailability(string eventTypeId, DateTime fromUtc, DateTime toUtc,
        SchedulingService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.GetAvailabilityAsync(eventTypeId, fromUtc, toUtc, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Book(BookSessionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        IValidator<BookSessionRequest> validator,
        SchedulingService service, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);
        var key = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxIdempotencyKeyLength)
            return SchedulingErrors.IdempotencyKeyRequired.ToProblem(http);
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();
        var athlete = await db.AthleteProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.CoachId == coachId, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (athlete is null || user?.FullName is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        var result = await service.BookAsync(coachId, athlete.Id, user.FullName, user.Email, key, request, ct);
        return result.IsSuccess ? Results.Created($"/api/v1/sessions/{result.Value.Id}", result.Value) : result.Error!.ToProblem(http);
    }

    private static IResult RefreshScheduling(ISchedulingJobScheduler scheduler)
    {
        scheduler.EnqueueReconciliation();
        return Results.Accepted();
    }

    private static async Task<IResult> List(DateTime? fromUtc, DateTime? toUtc, Guid? athleteProfileId,
        SessionStatus? status, int limit, string? cursor, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();
        var take = Math.Clamp(limit == 0 ? 30 : limit, 1, 100);
        var query = db.Sessions.AsNoTracking().Where(x => x.CoachId == coachId);
        if (principal.IsInRole(nameof(UserRole.Athlete)))
        {
            var ownId = await db.AthleteProfiles.Where(x => x.UserId == userId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
            if (ownId is null) return Results.Ok(new SessionPage([], null));
            query = query.Where(x => x.AthleteProfileId == ownId);
        }
        else if (athleteProfileId is not null) query = query.Where(x => x.AthleteProfileId == athleteProfileId);
        if (fromUtc is not null) query = query.Where(x => x.ScheduledStartUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.ScheduledStartUtc < toUtc);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (DateTime.TryParse(cursor, out var before)) query = query.Where(x => x.ScheduledStartUtc > before.ToUniversalTime());
        var rows = await query.OrderBy(x => x.ScheduledStartUtc).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? rows[take - 1].ScheduledStartUtc.ToString("O") : null;
        return Results.Ok(new SessionPage(rows.Take(take).Select(x => x.ToResponse()).ToArray(), next));
    }

    private static Task<IResult> Upcoming(int limit, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        List(clock.UtcNow, null, null, SessionStatus.Scheduled, limit == 0 ? 10 : limit, null, db, principal, ct);

    private static async Task<IResult> Detail(Guid id, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct);
        return session is null ? SchedulingErrors.SessionNotFound.ToProblem(http) : Results.Ok(session.ToResponse());
    }

    private static async Task<IResult> Cancel(Guid id, CancelSessionRequest request, SchedulingService service,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct, tracked: true);
        if (session is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        var result = await service.CancelAsync(session, request.Reason, ct);
        return result.IsSuccess ? Results.Ok(session.ToResponse()) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Reschedule(Guid id, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct);
        if (session?.RescheduleUrl is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        return Results.Ok(new RescheduleUrlResponse(session.RescheduleUrl));
    }

    private static async Task<IResult> ReceiveWebhook(HttpRequest request, AppDbContext db,
        ICalendlyWebhookVerifier verifier, ICalendlyWebhookParser parser, ISchedulingJobScheduler scheduler,
        IClock clock, HttpContext http, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync(ct);
        if (!verifier.IsValid(json, request.Headers["Calendly-Webhook-Signature"].FirstOrDefault(), clock.UtcNow))
            return SchedulingErrors.CalendlySignatureInvalid.ToProblem(http);
        CalendlyWebhookEnvelope envelope;
        try { envelope = parser.Parse(json); }
        catch (JsonException) { return Results.BadRequest(); }
        if (await db.CalendlyWebhookEvents.AnyAsync(x => x.IdempotencyKey == envelope.IdempotencyKey, ct)) return Results.Ok();
        db.CalendlyWebhookEvents.Add(CalendlyWebhookEvent.Receive(envelope.IdempotencyKey, envelope.EventType, json, clock.UtcNow));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Ok(); }
        scheduler.EnqueueWebhook(db.Entry(db.CalendlyWebhookEvents.Local.Single(x => x.IdempotencyKey == envelope.IdempotencyKey)).Entity.Id);
        return Results.Accepted();
    }

    private static async Task<Session?> OwnedSession(Guid id, AppDbContext db, ClaimsPrincipal principal,
        CancellationToken ct, bool tracked = false)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return null;
        var query = tracked ? db.Sessions.AsQueryable() : db.Sessions.AsNoTracking();
        query = query.Where(x => x.Id == id && x.CoachId == coachId);
        if (principal.IsInRole(nameof(UserRole.Athlete)))
            query = query.Where(x => db.AthleteProfiles.Any(a => a.Id == x.AthleteProfileId && a.UserId == userId));
        return await query.SingleOrDefaultAsync(ct);
    }
}
