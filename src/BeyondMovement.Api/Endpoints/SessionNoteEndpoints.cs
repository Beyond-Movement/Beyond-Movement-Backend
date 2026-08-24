using System.Security.Claims;
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
/// The coach's notes on a session. Admin-only throughout: the UI/UX document places these on
/// Session Details (Admin View), and nothing in it shows a coach's session notes to the athlete.
/// Opening them up later is additive; having shown them by mistake is not undoable.
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
                "A session holds many notes rather than one editable block: the screen offers add " +
                "as well as edit, and a record that can only be overwritten loses what was " +
                "written last time the first time something is added this time. Notes can be " +
                "added to a session in any status - the coach often writes them up after it has " +
                "been marked attended, and a cancelled session can still be worth a line saying " +
                "why.")
            .Produces<SessionNoteResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        notes.MapPut("/{noteId:guid}", Edit)
            .WithName("EditSessionNote")
            .WithSummary("Rewrite a note.")
            .WithDescription(
                "Replaces the text. createdAtUtc deliberately stays put so the history keeps its " +
                "order when a note written days ago is corrected today; updatedAtUtc moves. " +
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

        return app;
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

        var note = SessionNote.Write(sessionId, authorUserId, request.Content, clock.UtcNow);
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

        note.Revise(request.Content, clock.UtcNow);
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
