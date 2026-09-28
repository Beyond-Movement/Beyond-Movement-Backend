using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// The recurring sweep that makes sure no attachment object outlives its reason to exist.
/// <para>
/// Three kinds of row end up here: <b>abandoned uploads</b> (pending past their completion
/// window), <b>orphans</b> (their note or session was deleted at the database level, leaving a
/// null note id), and anything already <b>marked Deleting</b> whose object an earlier attempt
/// could not remove. Each has its object deleted first and its row second, so a storage outage
/// only postpones the work — the next run finds the same rows and tries again.
/// </para>
/// <para>
/// This is the application's cleanup. The bucket's lifecycle rule that aborts incomplete
/// multipart uploads is a separate, AWS-side safety net and does not replace it.
/// </para>
/// </summary>
public sealed class SessionNoteAttachmentCleanupJob(
    AppDbContext db, SessionNoteAttachmentService attachments, IClock clock,
    ILogger<SessionNoteAttachmentCleanupJob> logger)
{
    public const string RecurringJobId = "session-note-attachment-cleanup";
    public const int BatchSize = 200;

    /// <summary>
    /// No Hangfire retries: the job is idempotent and runs again on its schedule, and a retry
    /// storm against a storage outage helps nobody.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cutoff = now - SessionNoteAttachmentService.CompletionGrace;

        var toRetire = await db.SessionNoteAttachments
            .Where(x => x.Status != SessionNoteAttachmentStatus.Deleting
                        && (x.SessionNoteId == null
                            || (x.Status == SessionNoteAttachmentStatus.Pending && x.UploadExpiresAtUtc < cutoff)))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var attachment in toRetire) attachment.MarkForDeletion(now);
        if (toRetire.Count > 0) await db.SaveChangesAsync(ct);

        var doomed = await db.SessionNoteAttachments
            .Where(x => x.Status == SessionNoteAttachmentStatus.Deleting)
            .OrderBy(x => x.DeletionRequestedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        var purged = await attachments.PurgeAsync(doomed, ct);

        if (toRetire.Count > 0 || doomed.Count > 0)
            logger.LogInformation(
                "Attachment cleanup: {Retired} retired, {Purged} of {Doomed} purged",
                toRetire.Count, purged, doomed.Count);

        return purged;
    }
}
