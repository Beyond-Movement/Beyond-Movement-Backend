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
/// The coach's notes on a session, and the Session Notes History assembled from them.
/// <para>
/// Three reads over <b>one</b> set of rows, and one writer. <c>/sessions/{id}/notes</c> is the
/// running record of a single session, oldest first, and is the <b>only</b> place a note is
/// written. <c>/athletes/{id}/notes</c> is those same notes gathered across every session an
/// athlete has had, newest first, for the Admin. <c>/me/notes</c> is the identical history for the
/// athlete themselves. There is no second notes entity and no separate observation history — an
/// observation is one delivery type, and its notes are ordinary session notes.
/// </para>
/// <para>
/// <b>Writing stays Admin-only; reading no longer is</b> (client decision, 2026-09-21). Notes were
/// Admin-only when they were introduced, on the grounds that nothing in the product showed them to
/// the athlete. The client has since decided that session notes are shared, so the athlete reads
/// their own — <b>including notes written before that decision</b>. An athlete can never add, edit
/// or delete one.
/// </para>
/// </summary>
public static class SessionNoteEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapSessionNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/v1/sessions/{sessionId:guid}/notes")
            .WithTags("Session notes")
            .RequireAuthorization("AdminOnly");

        notes.MapGet(string.Empty, List)
            .WithName("ListSessionNotes")
            .WithSummary("The notes on one session, oldest first.")
            .WithDescription(
                "Oldest first, because they read as a running record of the session rather than " +
                "a feed. An empty list is normal. An unknown session, or one belonging to another " +
                "coach, is 404 SESSION_NOT_FOUND.")
            .Produces<IReadOnlyList<SessionNoteResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        notes.MapPost(string.Empty, Create)
            .WithName("CreateSessionNote")
            .WithSummary("Add a note to a session.")
            .WithDescription(
                "title AND content are both REQUIRED. title is what a history row shows first: " +
                "non-blank, trimmed, at most 200 characters. content is non-blank and at most " +
                "4000. " +
                "A session holds many notes rather than one editable block: the screen offers add " +
                "as well as edit, and a record that can only be overwritten loses what was " +
                "written last time the first time something is added this time. Notes can be " +
                "added to a session in any status - the coach often writes them up after it has " +
                "been marked attended, and a cancelled session can still be worth a line saying " +
                "why. " +
                "THE ATHLETE CAN READ THIS. Session notes are shared: whatever is written here " +
                "appears in the athlete's own GET /me/notes. They cannot change or delete it.")
            .Produces<SessionNoteResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        notes.MapPut("/{noteId:guid}", Edit)
            .WithName("EditSessionNote")
            .WithSummary("Rewrite a note.")
            .WithDescription(
                "Replaces BOTH title and content, and both are required every time - there is no " +
                "partial update, so sending only one is 400 VALIDATION_FAILED rather than a " +
                "change to that field alone. " +
                "createdAtUtc deliberately stays put so the history keeps its order when a note " +
                "written days ago is corrected today; updatedAtUtc moves, and comparing the two " +
                "is how a client shows that a note was edited. " +
                "404 SESSION_NOTE_NOT_FOUND covers a note that does not exist and one that " +
                "belongs to a different session alike.")
            .Produces<SessionNoteResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        notes.MapDelete("/{noteId:guid}", Delete)
            .WithName("DeleteSessionNote")
            .WithSummary("Remove a note.")
            .WithDescription(
                "204 with no body. Deleting a note that is already gone is 404 " +
                "SESSION_NOTE_NOT_FOUND rather than a silent success, so a client that thinks it " +
                "deleted something twice finds out.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        var history = app.MapGroup("/api/v1/athletes/{athleteId:guid}/notes")
            .WithTags("Session notes")
            .RequireAuthorization("AdminOnly");

        history.MapGet(string.Empty, History)
            .WithName("ListAthleteSessionNotes")
            .WithSummary("An athlete's Session Notes History, newest first.")
            .WithDescription(
                "Every session note belonging to one athlete, gathered across ALL their sessions " +
                "in one call - the Admin's Session Notes History screen. These are the same notes " +
                "POST and PUT /sessions/{sessionId}/notes write; there is no second notes system. " +
                "ALL DELIVERY TYPES appear together: Online, FaceToFace and Observation. An " +
                "observation is simply one sessionDeliveryType, so there is no separate " +
                "observation history and no isObservation flag - compare sessionDeliveryType to " +
                "\"Observation\" if the screen marks them. " +
                "athleteId is the athlete's USER id, matching every other /athletes/{athleteId} " +
                "route. An unknown one, or an athlete belonging to another coach, is 404 " +
                "ATHLETE_NOT_FOUND rather than an empty page, because an empty page is a real " +
                "answer and a bad id must not be mistaken for one. " +
                "PAGED in the usual envelope: items plus page, pageSize, totalCount, totalPages, " +
                "hasNextPage and hasPreviousPage. page starts at 1, pageSize defaults to 20 and " +
                "is capped at 100, and values outside the range are clamped rather than rejected. " +
                "Ordered by createdAtUtc DESCENDING with the id breaking ties, so the order is " +
                "total and a note cannot appear on two pages. createdAtUtc does NOT move when a " +
                "note is edited, so correcting an old note does not jump it to the top; " +
                "updatedAtUtc is what shows it was edited. " +
                "EVERY ROW CARRIES ITS SESSION - sessionId, sessionStartUtc, sessionEndUtc, " +
                "sessionDeliveryType, sessionStatus and sessionLocationOrPlatform - so the screen " +
                "renders a page without a second call per session, and sessionId is what links a " +
                "row back to GET /sessions/{sessionId}. " +
                "An athlete who has never had a note gets an EMPTY PAGE, not a 404.")
            .Produces<PagedResult<AthleteSessionNoteResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        var mine = app.MapGroup("/api/v1/me/notes")
            .WithTags("Session notes")
            .RequireAuthorization("AthleteOnly");

        mine.MapGet(string.Empty, MyHistory)
            .WithName("ListMySessionNotes")
            .WithSummary("The calling athlete's own Session Notes History, newest first.")
            .WithDescription(
                "The athlete's own view of what their coach wrote about their sessions. THE SAME " +
                "RESPONSE AS THE ADMIN'S GET /api/v1/athletes/{athleteId}/notes, item for item " +
                "and field for field, so the two screens share one model. " +
                "ALWAYS THE CALLER'S OWN - there is no athlete id in the route or the body, so " +
                "there is nothing to authorise beyond being signed in as an athlete, and one " +
                "athlete can never read another's notes. " +
                "READ-ONLY. There is no POST, PUT or DELETE here: notes are written by the Admin " +
                "on Session Details and nowhere else, and an athlete cannot add, edit or remove " +
                "one. " +
                "ALL DELIVERY TYPES appear together - Online, FaceToFace and Observation - and " +
                "EVERY note is included, those written before notes were shared with athletes " +
                "as well. " +
                "PAGED in the usual envelope, page from 1, pageSize 20 by default and capped at " +
                "100, clamped rather than rejected. Ordered by createdAtUtc DESCENDING with the " +
                "id breaking ties. " +
                "An athlete who has no notes, or who has not completed their profile yet, gets " +
                "an EMPTY PAGE rather than a 404.")
            .Produces<PagedResult<AthleteSessionNoteResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        return app;
    }

    /// <summary>
    /// The calling athlete's own history. The profile id comes from the token and there is no
    /// athlete id anywhere in the request, so this cannot be pointed at anyone else.
    /// <para>
    /// An athlete with no profile — registered but never completed — gets an empty page rather
    /// than an error, the same answer <c>GET /sessions</c> and <c>GET /me/observation-requests</c>
    /// give them.
    /// </para>
    /// </summary>
    private static async Task<IResult> MyHistory(
        SessionNoteHistoryReader reader, AppDbContext db, ClaimsPrincipal principal,
        CancellationToken ct,
        int page = 1, int pageSize = PagedResult<AthleteSessionNoteResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var (normalizedPage, normalizedSize) =
            PagedResult<AthleteSessionNoteResponse>.Normalize(page, pageSize);

        var athleteProfileId = await db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == userId && x.CoachId == coachId && x.DeletedAtUtc == null)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);

        if (athleteProfileId is null)
            return Results.Ok(new PagedResult<AthleteSessionNoteResponse>(
                [], normalizedPage, normalizedSize, 0));

        return Results.Ok(await reader.ReadForProfileAsync(
            coachId, athleteProfileId.Value, normalizedPage, normalizedSize, ct));
    }

    /// <summary>
    /// The athlete's whole Session Notes History, read in one query rather than by walking their
    /// sessions. The coach id comes from the token and never from the route, so an athlete
    /// belonging to somebody else does not resolve and the caller gets the same 404 as for an id
    /// that does not exist.
    /// </summary>
    private static async Task<IResult> History(
        Guid athleteId, SessionNoteHistoryReader reader, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct,
        int page = 1, int pageSize = PagedResult<AthleteSessionNoteResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var (normalizedPage, normalizedSize) =
            PagedResult<AthleteSessionNoteResponse>.Normalize(page, pageSize);

        var result = await reader.ReadAsync(coachId, athleteId, normalizedPage, normalizedSize, ct);

        return result is null
            ? PricingErrors.AthleteNotFound.ToProblem(http)
            : Results.Ok(result);
    }

    private static async Task<IResult> List(
        Guid sessionId, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!await SessionExists(sessionId, db, principal, ct))
            return SchedulingErrors.SessionNotFound.ToProblem(http);

        var notes = await db.SessionNotes.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .ToListAsync(ct);

        return Results.Ok(notes.Select(x => x.ToResponse()).ToArray());
    }

    private static async Task<IResult> Create(
        Guid sessionId, SaveSessionNoteRequest request, IValidator<SaveSessionNoteRequest> validator,
        AppDbContext db, IClock clock, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var authorUserId, out _)) return Results.Unauthorized();

        if (!await SessionExists(sessionId, db, principal, ct))
            return SchedulingErrors.SessionNotFound.ToProblem(http);

        var note = SessionNote.Write(
            sessionId, authorUserId, request.Title, request.Content, clock.UtcNow);
        db.SessionNotes.Add(note);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/sessions/{sessionId}/notes/{note.Id}", note.ToResponse());
    }

    private static async Task<IResult> Edit(
        Guid sessionId, Guid noteId, SaveSessionNoteRequest request,
        IValidator<SaveSessionNoteRequest> validator, AppDbContext db, IClock clock,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        var note = await OwnedNote(sessionId, noteId, db, principal, ct);

        if (note is null) return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        note.Revise(request.Title, request.Content, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return Results.Ok(note.ToResponse());
    }

    private static async Task<IResult> Delete(
        Guid sessionId, Guid noteId, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        var note = await OwnedNote(sessionId, noteId, db, principal, ct);

        if (note is null) return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        db.SessionNotes.Remove(note);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// The session must exist and belong to the caller's coach. Scoping by the coach id from the
    /// token is what keeps another coach's session a 404 rather than a note written onto it.
    /// </summary>
    private static Task<bool> SessionExists(
        Guid sessionId, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        principal.TryGetIdentity(out _, out var coachId)
            ? db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.CoachId == coachId, ct)
            : Task.FromResult(false);

    private static async Task<SessionNote?> OwnedNote(
        Guid sessionId, Guid noteId, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!await SessionExists(sessionId, db, principal, ct)) return null;

        return await db.SessionNotes.FirstOrDefaultAsync(x => x.Id == noteId && x.SessionId == sessionId, ct);
    }
}
