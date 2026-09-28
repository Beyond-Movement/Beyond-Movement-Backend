using BeyondMovement.Infrastructure;
using BeyondMovement.Infrastructure.Storage;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// Everything that touches a session note's images: issuing upload URLs, verifying what was
/// uploaded, signing download URLs and removing objects.
/// <para>
/// <b>Authorisation is the caller's job, and every method here assumes it is done.</b> The
/// endpoints resolve the note through the session's coach (Admin) or the athlete's own profile
/// (athlete) before calling in, exactly as the note endpoints themselves do; every query below is
/// then scoped to that note id, so an attachment id from another note never resolves.
/// </para>
/// <para>
/// Lives in the composition root because it needs both the Scheduling tables and object storage,
/// which is infrastructure a module may not reference.
/// </para>
/// </summary>
public sealed class SessionNoteAttachmentService(
    AppDbContext db,
    IObjectStorage storage,
    IOptions<StorageOptions> options,
    IClock clock,
    ILogger<SessionNoteAttachmentService> logger)
{
    /// <summary>
    /// How long after the upload URL expires a completion is still accepted. S3 checks a
    /// pre-signed URL's expiry when a request starts, so a large upload begun on a slow phone at
    /// the last second can legitimately finish — and be completed — a little after it.
    /// </summary>
    public static readonly TimeSpan CompletionGrace = TimeSpan.FromMinutes(15);

    private StorageOptions Options => options.Value;

    // --------------------------------------------------------------------- reads

    /// <summary>
    /// The committed images of each note, in order, each with a fresh download URL. One query for
    /// the whole page of notes, never one per note. A note with no images maps to an empty list.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SessionNoteAttachmentResponse>>> ForNotesAsync(
        IReadOnlyCollection<Guid> noteIds, CancellationToken ct)
    {
        var result = noteIds.Distinct().ToDictionary(
            id => id, _ => (IReadOnlyList<SessionNoteAttachmentResponse>)[]);

        if (result.Count == 0) return result;

        var ids = result.Keys.ToArray();
        var rows = await db.SessionNoteAttachments.AsNoTracking()
            .Where(x => x.SessionNoteId != null && ids.Contains(x.SessionNoteId.Value)
                        && x.Status == SessionNoteAttachmentStatus.Committed)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        foreach (var group in rows.GroupBy(x => x.SessionNoteId!.Value))
        {
            var signed = new List<SessionNoteAttachmentResponse>();
            foreach (var row in group) signed.Add(await SignAsync(row));
            result[group.Key] = signed;
        }

        return result;
    }

    public async Task<IReadOnlyList<SessionNoteAttachmentResponse>> ForNoteAsync(Guid noteId, CancellationToken ct) =>
        (await ForNotesAsync([noteId], ct))[noteId];

    /// <summary>A fresh download URL for one committed image on this note.</summary>
    public async Task<Result<SessionNoteAttachmentResponse>> DownloadUrlAsync(
        Guid noteId, Guid attachmentId, CancellationToken ct)
    {
        var row = await db.SessionNoteAttachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.SessionNoteId == noteId
                                       && x.Status == SessionNoteAttachmentStatus.Committed, ct);

        return row is null
            ? Result<SessionNoteAttachmentResponse>.Failure(SessionNoteAttachmentErrors.AttachmentNotFound)
            : Result<SessionNoteAttachmentResponse>.Success(await SignAsync(row));
    }

    // ------------------------------------------------------------------ upload

    /// <summary>
    /// Records a pending attachment and returns a pre-signed PUT for it.
    /// <para>
    /// The note row is locked for the duration, so two requests racing for the fifth slot are
    /// serialised and exactly one of them gets it. Pending uploads count toward the limit;
    /// abandoned ones stop counting once their completion window has passed.
    /// </para>
    /// </summary>
    public async Task<Result<SessionNoteAttachmentUploadResponse>> RequestUploadAsync(
        Guid noteId, Guid uploadedByUserId, RequestSessionNoteAttachmentUploadRequest request,
        CancellationToken ct)
    {
        var contentType = request.ContentType?.Trim().ToLowerInvariant();

        if (!SessionNoteAttachment.IsSupportedContentType(contentType))
            return Result<SessionNoteAttachmentUploadResponse>.Failure(
                SessionNoteAttachmentErrors.UnsupportedAttachmentType);

        if (request.SizeBytes <= 0 || request.SizeBytes > Options.MaxImageBytes)
            return Result<SessionNoteAttachmentUploadResponse>.Failure(
                SessionNoteAttachmentErrors.AttachmentTooLarge(Options.MaxImageBytes));

        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync(
            $"""select 1 from "SessionNotes" where "Id" = {noteId} for update""", ct);

        var live = await db.SessionNoteAttachments
            .Where(x => x.SessionNoteId == noteId && x.Status != SessionNoteAttachmentStatus.Deleting)
            .ToListAsync(ct);

        // Abandoned uploads free their slot here rather than waiting for the cleanup job.
        foreach (var stale in live.Where(x => IsAbandoned(x, now)))
            stale.MarkForDeletion(now);

        if (live.Count(x => x.Status != SessionNoteAttachmentStatus.Deleting) >= Options.MaxAttachmentsPerNote)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result<SessionNoteAttachmentUploadResponse>.Failure(
                SessionNoteAttachmentErrors.AttachmentLimitReached(Options.MaxAttachmentsPerNote));
        }

        // Every row the note has ever had, including ones being deleted, so a new image never
        // reuses a position.
        var sortOrder = await db.SessionNoteAttachments
            .Where(x => x.SessionNoteId == noteId)
            .MaxAsync(x => (int?)x.SortOrder, ct) is { } last ? last + 1 : 0;

        var attachment = SessionNoteAttachment.Request(
            noteId, contentType!, request.SizeBytes, uploadedByUserId, sortOrder,
            now, now.Add(Options.UploadUrlLifetime));

        // Signed before anything is saved: if storage is unreachable the exception propagates,
        // the transaction rolls back, and no slot is taken by an upload that cannot happen.
        var upload = await storage.PresignUploadAsync(
            attachment.StorageKey, attachment.ContentType, Options.UploadUrlLifetime);

        db.SessionNoteAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result<SessionNoteAttachmentUploadResponse>.Success(new SessionNoteAttachmentUploadResponse(
            attachment.Id,
            upload.Url,
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = attachment.ContentType },
            upload.ExpiresAtUtc,
            Options.MaxImageBytes));
    }

    /// <summary>
    /// Verifies the uploaded object and commits the attachment. Idempotent: completing a
    /// committed attachment returns it again.
    /// <para>
    /// The object must exist, be exactly the declared size and within the limit, carry the
    /// declared content type, and <b>begin with the real signature of that format</b> — the
    /// declared type alone is never trusted. An object that fails is deleted and the attachment
    /// discarded. An object that has simply not arrived yet leaves the attachment pending, so a
    /// client whose PUT failed can retry it while its URL is still valid.
    /// </para>
    /// </summary>
    public async Task<Result<SessionNoteAttachmentResponse>> CompleteAsync(
        Guid noteId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await db.SessionNoteAttachments
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.SessionNoteId == noteId
                                       && x.Status != SessionNoteAttachmentStatus.Deleting, ct);

        if (attachment is null)
            return Result<SessionNoteAttachmentResponse>.Failure(SessionNoteAttachmentErrors.AttachmentNotFound);

        if (attachment.Status == SessionNoteAttachmentStatus.Committed)
            return Result<SessionNoteAttachmentResponse>.Success(await SignAsync(attachment));

        if (IsAbandoned(attachment, clock.UtcNow))
            return await RejectAsync(attachment,
                "The upload window for this attachment has closed. Request a new upload.", ct);

        var info = await storage.GetInfoAsync(attachment.StorageKey, ct);

        if (info is null)
            return Result<SessionNoteAttachmentResponse>.Failure(SessionNoteAttachmentErrors.AttachmentUploadInvalid(
                "Nothing has been uploaded for this attachment yet."));

        if (info.SizeBytes <= 0 || info.SizeBytes > Options.MaxImageBytes)
            return await RejectAsync(attachment, "The uploaded image is empty or too large.", ct);

        if (info.SizeBytes != attachment.DeclaredSizeBytes)
            return await RejectAsync(attachment, "The uploaded image is not the size that was declared.", ct);

        if (!SameMediaType(info.ContentType, attachment.ContentType))
            return await RejectAsync(attachment, "The uploaded image is not the type that was declared.", ct);

        var header = await storage.ReadPrefixAsync(
            attachment.StorageKey, SessionNoteAttachment.SignatureLength, ct);

        if (!SessionNoteAttachment.SignatureMatches(attachment.ContentType, header))
            return await RejectAsync(attachment, "The uploaded file is not a valid image of the declared type.", ct);

        attachment.Commit(info.SizeBytes, clock.UtcNow);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another completion of the same attachment won the race. Its answer is ours.
            db.ChangeTracker.Clear();
            return await DownloadUrlAsync(noteId, attachmentId, ct);
        }

        return Result<SessionNoteAttachmentResponse>.Success(await SignAsync(attachment));
    }

    private async Task<Result<SessionNoteAttachmentResponse>> RejectAsync(
        SessionNoteAttachment attachment, string why, CancellationToken ct)
    {
        attachment.MarkForDeletion(clock.UtcNow);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<SessionNoteAttachmentResponse>.Failure(SessionNoteAttachmentErrors.AttachmentNotFound);
        }

        logger.LogWarning("Rejected upload for attachment {AttachmentId}: {Reason}", attachment.Id, why);
        await PurgeAsync([attachment], ct);

        return Result<SessionNoteAttachmentResponse>.Failure(SessionNoteAttachmentErrors.AttachmentUploadInvalid(why));
    }

    // ------------------------------------------------------------------ delete

    /// <summary>
    /// Removes an image, pending or committed. It disappears from every read at once; its object
    /// is deleted now if storage is reachable and by the cleanup job otherwise.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid noteId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await db.SessionNoteAttachments
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.SessionNoteId == noteId
                                       && x.Status != SessionNoteAttachmentStatus.Deleting, ct);

        if (attachment is null) return Result.Failure(SessionNoteAttachmentErrors.AttachmentNotFound);

        attachment.MarkForDeletion(clock.UtcNow);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure(SessionNoteAttachmentErrors.AttachmentNotFound);
        }

        await PurgeAsync([attachment], ct);
        return Result.Success();
    }

    /// <summary>
    /// Deletes each row's object and then the row — in that order, so the storage key is never
    /// lost before the object is gone. A storage failure leaves that row for the next attempt.
    /// The rows must already be marked <see cref="SessionNoteAttachmentStatus.Deleting"/> and be
    /// tracked by this context.
    /// </summary>
    /// <returns>How many rows were fully removed.</returns>
    public async Task<int> PurgeAsync(IReadOnlyList<SessionNoteAttachment> attachments, CancellationToken ct)
    {
        var purged = 0;

        foreach (var attachment in attachments)
        {
            if (attachment.Status != SessionNoteAttachmentStatus.Deleting)
                throw new InvalidOperationException("Only an attachment marked for deletion can be purged.");

            try
            {
                await storage.DeleteAsync(attachment.StorageKey, ct);
            }
            catch (StorageUnavailableException)
            {
                logger.LogWarning(
                    "Storage delete for attachment {AttachmentId} failed; left for the cleanup job",
                    attachment.Id);
                continue;
            }

            db.SessionNoteAttachments.Remove(attachment);

            try
            {
                await db.SaveChangesAsync(ct);
                purged++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Removed concurrently by another purge. The object is gone either way.
                db.Entry(attachment).State = EntityState.Detached;
            }
        }

        return purged;
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>A pending upload whose completion window has closed. It can never be committed.</summary>
    public static bool IsAbandoned(SessionNoteAttachment attachment, DateTime nowUtc) =>
        attachment.Status == SessionNoteAttachmentStatus.Pending
        && nowUtc > attachment.UploadExpiresAtUtc + CompletionGrace;

    private static bool SameMediaType(string? stored, string declared) =>
        stored is not null
        && string.Equals(stored.Split(';')[0].Trim(), declared, StringComparison.OrdinalIgnoreCase);

    private async Task<SessionNoteAttachmentResponse> SignAsync(SessionNoteAttachment x)
    {
        var download = await storage.PresignDownloadAsync(x.StorageKey, Options.DownloadUrlLifetime);

        return new SessionNoteAttachmentResponse(
            x.Id, x.SessionNoteId!.Value, x.ContentType, x.SizeBytes!.Value, x.SortOrder, x.CreatedAtUtc,
            download.Url, download.ExpiresAtUtc);
    }
}
