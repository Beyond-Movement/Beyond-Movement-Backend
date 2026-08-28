using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Attendance;

/// <summary>
/// Marking a session attended, and the package deduction that goes with it — architecture
/// section 6.7. It writes to both Scheduling and Packages, so by CLAUDE.md section 4 it belongs
/// here in the composition root rather than in either module.
/// <para>
/// <b>Exactly-once</b> is the single most important invariant in the product, and it is held by
/// three independent things rather than by this method being careful:
/// </para>
/// <list type="number">
/// <item><see cref="Session.Resolve"/> refuses any session that is not Scheduled, so a second
/// request never reaches the package at all.</item>
/// <item>Both rows carry an <c>xmin</c> row version. If two requests get past the status check at
/// the same instant, one <c>UPDATE</c> matches no row and EF raises a concurrency exception,
/// which becomes CONCURRENCY_CONFLICT. The loser deducts nothing.</item>
/// <item>A check constraint refuses <c>UsedSessions &gt; TotalSessions</c> whatever the
/// application believes.</item>
/// </list>
/// <para>
/// The architecture's sequence diagram writes this with <c>SELECT … FOR UPDATE</c>. The row
/// versions give the same guarantee — the second writer is rejected rather than made to wait —
/// without hand-written SQL for every read, and they are already how <see cref="Session"/> is
/// configured. The difference the caller sees is a 409 instead of a short block, which for a
/// double-tapped button is the better answer anyway.
/// </para>
/// </summary>
public sealed class AttendanceService(AppDbContext db, IClock clock, IAuditLogger audit)
{
    public async Task<Result<AttendanceResponse>> ResolveAsync(
        Guid coachId, Guid actorUserId, Guid sessionId, AttendanceOutcome outcome,
        bool? deductSession, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CoachId == coachId, ct);

        if (session is null)
            return Result<AttendanceResponse>.Failure(SchedulingErrors.SessionNotFound);

        var athleteName = await AthleteNameAsync(session.AthleteProfileId, ct);

        if (athleteName is null)
            return Result<AttendanceResponse>.Failure(SchedulingErrors.SessionNotFound);

        // Resolve rejects these states too, but this guard must run before package lookup. If the
        // first request consumed the package's final session, a retry still reports that the
        // session was already resolved rather than incorrectly claiming there is no package.
        var alreadyResolved = session.Status switch
        {
            SessionStatus.Attended => SchedulingErrors.SessionAlreadyAttended,
            SessionStatus.NoShow => SchedulingErrors.SessionAlreadyResolved,
            SessionStatus.Cancelled => SchedulingErrors.SessionCancelled,
            _ => null
        };

        if (alreadyResolved is not null)
            return Result<AttendanceResponse>.Failure(alreadyResolved);

        var sessionOutcome = outcome switch
        {
            AttendanceOutcome.Attended => SessionStatus.Attended,
            AttendanceOutcome.NoShow => SessionStatus.NoShow,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown attendance outcome.")
        };

        // Attended defers to the session: one for an ordinary session (BR-05), and for an
        // observation the choice the Admin recorded when creating it (BR-07). A no-show is the
        // coach's explicit decision for this one session; endpoint validation guarantees it is
        // present. Two different deductSession fields, deliberately: one decided then, one now.
        var consumed = outcome == AttendanceOutcome.NoShow
            ? deductSession == true ? 1 : 0
            : session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false);

        // The package is only fetched when something is actually going to be taken off it. A
        // non-deducting observation and a non-deducting no-show are recorded for an athlete who has no
        // package at all, which is a real situation and not an error.
        PurchasedPackage? package = null;

        if (consumed > 0)
        {
            package = await db.PurchasedPackages.FirstOrDefaultAsync(x =>
                x.AthleteProfileId == session.AthleteProfileId
                && x.Status == PurchasedPackageStatus.Active, ct);

            if (package is null)
                return Result<AttendanceResponse>.Failure(AttendanceErrors.ActivePackageNotFound);
        }

        // One transaction over both writes. Either the session records that it consumed a
        // session and the package records that it lost one, or neither happened — a session
        // marked attended against a balance that never moved is the failure this prevents.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var resolved = session.Resolve(sessionOutcome, consumed, actorUserId, clock.UtcNow);

        if (resolved.IsFailure)
            return Result<AttendanceResponse>.Failure(resolved.Error!);

        if (package is not null)
        {
            var deduction = package.Consume(consumed, clock.UtcNow);

            if (deduction.IsFailure)
                return Result<AttendanceResponse>.Failure(deduction.Error!);

            session.AttachToPackage(package.Id);
        }

        try
        {
            await db.SaveChangesAsync(ct);

            // Consumed value moving is exactly what the audit log exists for. Inside the
            // transaction, so a rolled-back deduction cannot leave behind a log entry claiming
            // it happened.
            await audit.WriteAsync($"Session{sessionOutcome}", actorUserId,
                $"session={session.Id} athleteProfile={session.AthleteProfileId} consumed={consumed}" +
                (package is null ? string.Empty : $" package={package.Id} remaining={package.RemainingSessions}"), ct);

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return Result<AttendanceResponse>.Failure(AttendanceErrors.ConcurrencyConflict);
        }

        return Result<AttendanceResponse>.Success(new AttendanceResponse(
            session.ToResponse(athleteName),
            consumed,
            package?.ToResponse(),
            package is null ? null : await ProgressAsync(session, package, ct)));
    }

    /// <summary>
    /// Where a session sits in its package. Returns null when the session belongs to no package,
    /// which is the normal state for an athlete between packages.
    /// </summary>
    public async Task<SessionPackageProgress?> ProgressAsync(
        Session session, PurchasedPackage? package, CancellationToken ct)
    {
        // A resolved session names the package it consumed from; an unresolved one has to ask
        // which package is active now, since that is the one it would consume from.
        package ??= session.PackageId is { } packageId
            ? await db.PurchasedPackages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == packageId, ct)
            : await db.PurchasedPackages.AsNoTracking().FirstOrDefaultAsync(x =>
                x.AthleteProfileId == session.AthleteProfileId
                && x.Status == PurchasedPackageStatus.Active, ct);

        if (package is null)
            return null;

        int? number;

        if (session.ConsumedSessionCount > 0 && session.PackageId == package.Id)
        {
            // Its own position: how many of this package's sessions had been consumed by the
            // time this one was, itself included. Ordered by when they were attended, which is
            // the order the coach used them in, not the order they were booked.
            number = await db.Sessions.AsNoTracking().CountAsync(x =>
                x.PackageId == package.Id
                && x.ConsumedSessionCount > 0
                && (x.AttendedAtUtc < session.AttendedAtUtc
                    || (x.AttendedAtUtc == session.AttendedAtUtc && x.Id == session.Id)), ct);
        }
        else
        {
            // Not resolved yet, or resolved without consuming. Only the first has a position to
            // predict; a non-deducting observation never takes one, and saying "Session 7 of 12" about
            // it would be wrong in a way nobody would catch.
            number = session.Status == SessionStatus.Scheduled && package.RemainingSessions > 0
                ? package.UsedSessions + 1
                : null;
        }

        return new SessionPackageProgress(package.Id, package.Name, number,
            package.TotalSessions, package.UsedSessions, package.RemainingSessions);
    }

    /// <summary>
    /// The same join <c>SchedulingEndpoints</c> makes, for the same reason: sessions cannot see
    /// users, so the athlete's display name is composed here.
    /// </summary>
    private Task<string?> AthleteNameAsync(Guid athleteProfileId, CancellationToken ct) =>
        (from profile in db.AthleteProfiles
         join user in db.Users on profile.UserId equals user.Id
         where profile.Id == athleteProfileId
         select user.FullName ?? user.Email).SingleOrDefaultAsync(ct);
}
