using System.Security.Claims;
using BeyondMovement.Api.Scheduling;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Contracts;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Images attached to session notes.
/// <para>
/// <b>The bytes never pass through the API.</b> The Admin asks for an upload, PUTs the file
/// straight to private storage with the short-lived URL it is given, then calls complete; the API
/// verifies what actually arrived before anyone can see it. Reads hand out short-lived download
/// URLs, never a permanent or public address.
/// </para>
/// <para>
/// Authorisation is the note's own. The Admin's routes resolve the note through
/// <see cref="SessionNoteEndpoints.OwnedNote"/>, exactly as editing the note does; the athlete's
/// route resolves it through their own profile, exactly as <c>GET /me/notes</c> does. Only then is
/// the attachment looked up, and only within that note — an attachment id on its own grants
/// nothing.
/// </para>
/// </summary>
public static class SessionNoteAttachmentEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapSessionNoteAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/sessions/{sessionId:guid}/notes/{noteId:guid}/attachments")
            .WithTags("Session notes")
            .RequireAuthorization("AdminOnly");

        admin.MapPost(string.Empty, RequestUpload)
            .WithName("RequestSessionNoteAttachmentUpload")
            .WithSummary("Start attaching an image to a note: returns a short-lived upload URL.")
            .WithDescription(
                "Step 1 of 3. Save the note first, then call this with the image's contentType " +
                "(image/jpeg, image/png or image/webp - HEIC is NOT accepted, re-encode to JPEG) " +
                "and its exact sizeBytes (1 byte to 10 MB). " +
                "Step 2: PUT the raw bytes to uploadUrl with EXACTLY the requiredHeaders, before " +
                "uploadUrlExpiresAtUtc (5 minutes). No Authorization header on that PUT - the URL " +
                "is the permission. Never log or persist the URL. " +
                "Step 3: POST .../attachments/{attachmentId}/complete. Until then the image is " +
                "invisible to every read. " +
                "A note holds at most 5 images, and uploads still in progress count, so a sixth " +
                "request is 409 ATTACHMENT_LIMIT_REACHED. An abandoned upload frees its slot " +
                "about 20 minutes after it was requested. " +
                "400 UNSUPPORTED_ATTACHMENT_TYPE, 400 ATTACHMENT_TOO_LARGE (also for sizeBytes " +
                "<= 0), 404 SESSION_NOTE_NOT_FOUND for a note that is not on this session or a " +
                "session that is not the caller's, 503 STORAGE_UNAVAILABLE.")
            .Produces<SessionNoteAttachmentUploadResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        admin.MapPost("/{attachmentId:guid}/complete", Complete)
            .WithName("CompleteSessionNoteAttachmentUpload")
            .WithSummary("Finish an upload: the API verifies the stored image and makes it visible.")
            .WithDescription(
                "No body. The API checks the object that actually arrived: it must exist, be " +
                "exactly the declared size (and at most 10 MB), have the declared content type, " +
                "and START WITH THE REAL SIGNATURE of a JPEG, PNG or WebP file. On success the " +
                "attachment is committed and returned with a download URL. " +
                "IDEMPOTENT: completing an attachment that is already committed returns it again " +
                "with 200, so a retry after a timeout is safe. " +
                "409 ATTACHMENT_UPLOAD_INVALID when verification fails. If nothing has been " +
                "uploaded yet the attachment stays pending and the PUT may be retried while its " +
                "URL is valid; in every other case (wrong size, type or file signature, or the " +
                "upload window has closed) the stored object is deleted, the attachment is gone, " +
                "and the client must request a new upload. " +
                "404 ATTACHMENT_NOT_FOUND for an unknown, deleted or other note's attachment. " +
                "503 STORAGE_UNAVAILABLE - nothing changed, retry.")
            .Produces<SessionNoteAttachmentResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        admin.MapDelete("/{attachmentId:guid}", Delete)
            .WithName("DeleteSessionNoteAttachment")
            .WithSummary("Remove an image from a note.")
            .WithDescription(
                "204 with no body. Works on a committed image and on an upload still pending. " +
                "The image disappears from every read immediately; its stored object is removed " +
                "now, or by the background cleanup if storage is briefly unreachable. Deleting " +
                "one that is already gone is 404 ATTACHMENT_NOT_FOUND.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        admin.MapGet("/{attachmentId:guid}/download-url", AdminDownloadUrl)
            .WithName("GetSessionNoteAttachmentDownloadUrl")
            .WithSummary("A fresh download URL for one image, when the one from a note read has expired.")
            .WithDescription(
                "Returns the attachment with a new downloadUrl valid for 15 minutes. Committed " +
                "images only; anything else is 404 ATTACHMENT_NOT_FOUND.")
            .Produces<SessionNoteAttachmentResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        var mine = app.MapGroup("/api/v1/me/notes/{noteId:guid}/attachments")
            .WithTags("Session notes")
            .RequireAuthorization("AthleteOnly");

        mine.MapGet("/{attachmentId:guid}/download-url", AthleteDownloadUrl)
            .WithName("GetMySessionNoteAttachmentDownloadUrl")
            .WithSummary("A fresh download URL for an image on one of the caller's own notes.")
            .WithDescription(
                "The athlete's counterpart of the Admin route, READ-ONLY - an athlete can never " +
                "upload or delete an image. The note must be one that appears in the caller's " +
                "own GET /me/notes; any other note, including another athlete's, is 404 " +
                "SESSION_NOTE_NOT_FOUND, exactly as for an id that does not exist. An attachment " +
                "that is not a committed image on that note is 404 ATTACHMENT_NOT_FOUND.")
            .Produces<SessionNoteAttachmentResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        return app;
    }

    private static async Task<IResult> RequestUpload(
        Guid sessionId, Guid noteId, RequestSessionNoteAttachmentUploadRequest request,
        SessionNoteAttachmentService attachments, AppDbContext db, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out _)) return Results.Unauthorized();

        if (await SessionNoteEndpoints.OwnedNote(sessionId, noteId, db, principal, ct) is null)
            return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        var result = await attachments.RequestUploadAsync(noteId, userId, request, ct);

        return result.IsSuccess
            ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
            : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Complete(
        Guid sessionId, Guid noteId, Guid attachmentId, SessionNoteAttachmentService attachments,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (await SessionNoteEndpoints.OwnedNote(sessionId, noteId, db, principal, ct) is null)
            return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        var result = await attachments.CompleteAsync(noteId, attachmentId, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Delete(
        Guid sessionId, Guid noteId, Guid attachmentId, SessionNoteAttachmentService attachments,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (await SessionNoteEndpoints.OwnedNote(sessionId, noteId, db, principal, ct) is null)
            return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        var result = await attachments.DeleteAsync(noteId, attachmentId, ct);

        return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> AdminDownloadUrl(
        Guid sessionId, Guid noteId, Guid attachmentId, SessionNoteAttachmentService attachments,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (await SessionNoteEndpoints.OwnedNote(sessionId, noteId, db, principal, ct) is null)
            return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        var result = await attachments.DownloadUrlAsync(noteId, attachmentId, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> AthleteDownloadUrl(
        Guid noteId, Guid attachmentId, SessionNoteAttachmentService attachments,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (await SessionNoteEndpoints.AthleteReadableNoteId(noteId, db, principal, ct) is null)
            return SchedulingErrors.SessionNoteNotFound.ToProblem(http);

        var result = await attachments.DownloadUrlAsync(noteId, attachmentId, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }
}
