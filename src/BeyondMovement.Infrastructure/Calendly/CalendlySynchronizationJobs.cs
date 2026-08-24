using System.Text.Json;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling.Calendly;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Features;
using BeyondMovement.SharedKernel;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Infrastructure.Calendly;

public sealed class HangfireSchedulingJobScheduler(IBackgroundJobClient jobs) : ISchedulingJobScheduler
{
    public void EnqueueWebhook(Guid webhookId) => jobs.Enqueue<CalendlySynchronizationJobs>(x =>
        x.ProcessWebhookAsync(webhookId, CancellationToken.None));
    public void EnqueueReconciliation() => jobs.Enqueue<CalendlySynchronizationJobs>(x =>
        x.ReconcileAsync(CancellationToken.None));
}

public sealed class DisabledSchedulingJobScheduler : ISchedulingJobScheduler
{
    public void EnqueueWebhook(Guid webhookId) { }
    public void EnqueueReconciliation() { }
}

public sealed class CalendlySynchronizationJobs(
    AppDbContext db, ICalendlyClient client, ICalendlyWebhookParser parser,
    SchedulingService scheduling, IClock clock, ILogger<CalendlySynchronizationJobs> logger)
{
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = new[] { 60, 300, 900, 3600, 21600 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task ProcessWebhookAsync(Guid webhookId, CancellationToken ct)
    {
        var raw = await db.CalendlyWebhookEvents.SingleOrDefaultAsync(x => x.Id == webhookId, ct);
        if (raw is null || raw.Status == WebhookProcessingStatus.Processed) return;
        raw.Begin();
        await db.SaveChangesAsync(ct);
        try
        {
            var envelope = parser.Parse(raw.PayloadJson);
            var athlete = await ResolveAthlete(envelope.Invitee.Email, ct);
            if (athlete is null)
            {
                await RecordUnmatched(envelope.Invitee, "No athlete has the invitee email.", ct);
                raw.Complete(clock.UtcNow);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (envelope.EventType == "invitee.canceled")
            {
                var session = await db.Sessions.SingleOrDefaultAsync(x =>
                    x.CalendlyInviteeUri == envelope.Invitee.Uri ||
                    (envelope.Invitee.OldInviteeUri != null && x.CalendlyInviteeUri == envelope.Invitee.OldInviteeUri), ct);
                if (session is null) await RecordUnmatched(envelope.Invitee, "Cancellation has no local session.", ct);
                else if (session.Cancel(clock.UtcNow, envelope.CancellationReason))
                    await AddChange(session, SchedulingChangeType.Cancelled, envelope.Invitee.Uri, ct);
            }
            else if (envelope.EventType == "invitee.created")
            {
                try { await scheduling.UpsertAsync(athlete.Value.CoachId, athlete.Value.ProfileId, envelope.Invitee, ct); }
                catch (UnmatchedCalendlyEventTypeException)
                { await RecordUnmatched(envelope.Invitee, "Calendly event type is not configured.", ct); }
            }
            raw.Complete(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JsonException or DbUpdateException or InvalidOperationException)
        {
            raw.Fail(ex.Message);
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Calendly webhook {WebhookId} failed on attempt {Attempt}.", raw.Id, raw.Attempts);
            throw;
        }
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var run = CalendlyReconciliationRun.Start(clock.UtcNow);
        db.CalendlyReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var created = 0; var updated = 0; var cancelled = 0; var flagged = 0;
        try
        {
            var from = clock.UtcNow.AddDays(-7); var to = clock.UtcNow.AddDays(60);
            var remote = await client.GetScheduledInviteesAsync(from, to, ct);
            var remoteUris = remote.Select(x => x.Uri).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var invitee in remote)
            {
                var athlete = await ResolveAthlete(invitee.Email, ct);
                if (athlete is null) { await RecordUnmatched(invitee, "No athlete has the invitee email.", ct); flagged++; continue; }
                var local = await db.Sessions.SingleOrDefaultAsync(x => x.CalendlyInviteeUri == invitee.Uri ||
                    (invitee.OldInviteeUri != null && x.CalendlyInviteeUri == invitee.OldInviteeUri), ct);
                if (string.Equals(invitee.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    if (local is not null && local.Cancel(clock.UtcNow, "Calendly reconciliation"))
                    { await AddChange(local, SchedulingChangeType.Cancelled, invitee.Uri, ct); cancelled++; }
                    continue;
                }
                try
                {
                    var change = await scheduling.UpsertAsync(athlete.Value.CoachId, athlete.Value.ProfileId, invitee, ct);
                    if (local is null) created++; else if (change == SchedulingChangeType.Rescheduled) updated++;
                }
                catch (UnmatchedCalendlyEventTypeException)
                { await RecordUnmatched(invitee, "Calendly event type is not configured.", ct); flagged++; }
            }
            var missing = await db.Sessions.Where(x => x.Status == SessionStatus.Scheduled &&
                x.ScheduledStartUtc >= from && x.ScheduledStartUtc <= to && !remoteUris.Contains(x.CalendlyInviteeUri)).ToListAsync(ct);
            foreach (var session in missing)
            {
                if (!await db.CalendlyUnmatchedBookings.AnyAsync(x => x.CalendlyInviteeUri == session.CalendlyInviteeUri, ct))
                    db.CalendlyUnmatchedBookings.Add(CalendlyUnmatchedBooking.Record(session.CalendlyEventUri,
                        session.CalendlyInviteeUri, session.CalendlyEventTypeUri, "unknown@local.invalid",
                        "Local scheduled session was not returned by Calendly; review required.", clock.UtcNow));
                flagged++;
            }
            run.Complete(clock.UtcNow, remote.Count, created, updated, cancelled, flagged);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Fail(clock.UtcNow, ex.Message); await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Calendly reconciliation {RunId} failed.", run.Id); throw;
        }
    }

    private async Task<(Guid ProfileId, Guid CoachId)?> ResolveAthlete(string email, CancellationToken ct)
    {
        var value = await (from user in db.Users.AsNoTracking()
            join athlete in db.AthleteProfiles.AsNoTracking() on user.Id equals athlete.UserId
            where user.Role == UserRole.Athlete && user.Email == email.ToLowerInvariant()
            select new { athlete.Id, athlete.CoachId }).SingleOrDefaultAsync(ct);
        return value is null ? null : (value.Id, value.CoachId);
    }

    private async Task RecordUnmatched(CalendlyInvitee invitee, string reason, CancellationToken ct)
    {
        if (!await db.CalendlyUnmatchedBookings.AnyAsync(x => x.CalendlyInviteeUri == invitee.Uri, ct))
            db.CalendlyUnmatchedBookings.Add(CalendlyUnmatchedBooking.Record(invitee.EventUri, invitee.Uri,
                invitee.EventTypeUri, invitee.Email, reason, clock.UtcNow));
    }

    private async Task AddChange(Session session, SchedulingChangeType type, string providerIdentity, CancellationToken ct)
    {
        var key = $"session:{session.Id}:{type}:{providerIdentity}";
        if (!await db.SchedulingChanges.AnyAsync(x => x.DedupKey == key, ct))
            db.SchedulingChanges.Add(SchedulingChange.Record(session.Id, type, providerIdentity, clock.UtcNow));
    }
}
