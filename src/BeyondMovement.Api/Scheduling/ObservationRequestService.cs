using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// The athlete's Observation Request and the Admin's decision on it.
/// <para>
/// Both rows an acceptance writes — the request and the session — belong to the Scheduling
/// module, so this is not a cross-module write. It lives here for the other reason
/// <c>AttendanceService</c> does: it needs a transaction, and <c>ISchedulingDbContext</c>
/// deliberately exposes only its DbSets. The athlete's profile id and display name are resolved
/// by the endpoint and passed in, exactly as <c>SchedulingService.BookAsync</c> takes the
/// athlete's name and email.
/// </para>
/// <para>
/// <b>Nothing here touches Calendly.</b> An observation is arranged in person and never appears
/// on a booking page (A-03), so accepting a request creates a purely local session, and
/// cancelling it later goes down the branch in <c>SchedulingService.CancelAsync</c> that skips
/// the Calendly call because there is no event to cancel.
/// </para>
/// </summary>
public sealed class ObservationRequestService(AppDbContext db, IClock clock, IAuditLogger audit)
{
    /// <summary>
    /// The athlete asks to be observed. Always their own — the profile id comes from the token,
    /// never the body, so a request cannot be filed against anyone else.
    /// <para>
    /// Deliberately <b>no limit of one pending request per athlete</b>, which is where this
    /// parts company with <c>PackagePurchase</c>. You buy one package at a time, but an athlete
    /// may have two competitions coming up, and each is a separate thing to ask for.
    /// </para>
    /// <para>
    /// No package is required, and none is touched. Creating an observation has never needed one
    /// — the balance moves at Mark as Attended and nowhere else (BR-04).
    /// </para>
    /// </summary>
    public async Task<ObservationRequest> CreateAsync(
        Guid coachId, Guid athleteProfileId, SaveObservationRequestRequest request, CancellationToken ct)
    {
        var observationRequest = ObservationRequest.Create(
            coachId, athleteProfileId, request.RequestedStartUtc,
            request.RequestedDurationMinutes ?? ObservationRequest.DefaultDurationMinutes,
            request.Location, request.Details, clock.UtcNow);

        db.ObservationRequests.Add(observationRequest);
        await db.SaveChangesAsync(ct);
        return observationRequest;
    }

    /// <summary>
    /// The athlete edits what they asked for, or the Admin adjusts it before accepting. The
    /// caller has already established who may touch this row; the only rule left is the one the
    /// entity keeps, that it is still Pending.
    /// </summary>
    public async Task<Result> ReviseAsync(
        ObservationRequest request, SaveObservationRequestRequest revision, CancellationToken ct)
    {
        var revised = request.Revise(
            revision.RequestedStartUtc,
            revision.RequestedDurationMinutes ?? ObservationRequest.DefaultDurationMinutes,
            revision.Location, revision.Details, clock.UtcNow);

        if (revised.IsFailure) return revised;

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>The athlete withdraws their request, or the Admin refuses it. No session either way.</summary>
    public async Task<Result> ResolveAsync(
        ObservationRequest request, ObservationRequestDecision decision, Guid actorUserId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var resolved = decision == ObservationRequestDecision.Cancelled
            ? request.Cancel(actorUserId, now)
            : request.Decline(actorUserId, now);

        if (resolved.IsFailure) return resolved;

        await audit.WriteAsync(
            decision == ObservationRequestDecision.Cancelled
                ? "ObservationRequestCancelled"
                : "ObservationRequestDeclined",
            actorUserId,
            $"observationRequest={request.Id} athleteProfile={request.AthleteProfileId}",
            ct);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The Admin accepts, and the observation becomes a real session in the same transaction —
    /// so an Accepted request without its session cannot be observed, whatever fails.
    /// <para>
    /// <b>A repeat is 409 <c>OBSERVATION_REQUEST_NOT_PENDING</c>, not a second session.</b>
    /// Three things hold that, in the order they act: the row lock taken below, so a concurrent
    /// repeat waits and then reads a row that is already Accepted; the Pending guard inside
    /// <see cref="ObservationRequest.Accept"/>; and the <c>xmin</c> row version, which catches
    /// two callers that somehow got past both.
    /// </para>
    /// <para>
    /// Any field the Admin sent replaces what the athlete asked for; anything they left null is
    /// accepted as requested. <paramref name="request"/>'s <c>deductSession</c> is the one field
    /// with no fallback — BR-07 is the Admin's choice to make, and the athlete never had one to
    /// inherit. Nothing is deducted now: the session is created Scheduled, and a booking never
    /// deducts (BR-04).
    /// </para>
    /// </summary>
    public async Task<Result<ObservationRequestAcceptance>> AcceptAsync(
        Guid coachId, Guid requestId, Guid actorUserId,
        AcceptObservationRequestRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialises concurrent acceptances of the same request. The second blocks here until
        // the first commits, then reads the row again and finds it Accepted - which is what
        // turns a double tap into one session and one 409.
        await db.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "ObservationRequests" WHERE "Id" = {requestId} FOR UPDATE""", ct);

        var observationRequest = await db.ObservationRequests
            .FirstOrDefaultAsync(x => x.Id == requestId && x.CoachId == coachId, ct);

        // Another coach's request is a 404, the same as one that does not exist.
        if (observationRequest is null)
            return Result<ObservationRequestAcceptance>.Failure(SchedulingErrors.ObservationRequestNotFound);

        if (observationRequest.Status != ObservationRequestStatus.Pending)
            return Result<ObservationRequestAcceptance>.Failure(SchedulingErrors.ObservationRequestNotPending);

        // The Admin's edits are applied to the request before it is accepted, so the row records
        // what was actually agreed rather than what was originally asked for - and the session
        // and the request cannot then disagree about the time and place of one observation.
        var startUtc = request.RequestedStartUtc ?? observationRequest.RequestedStartUtc;
        var durationMinutes = request.RequestedDurationMinutes ?? observationRequest.RequestedDurationMinutes;
        var location = request.Location ?? observationRequest.Location;
        var details = request.Details ?? observationRequest.Details;

        var revised = observationRequest.Revise(startUtc, durationMinutes, location, details, clock.UtcNow);

        // Unreachable: the status was checked under the lock immediately above. Surfaced rather
        // than swallowed so a later change that drops that check cannot hide it.
        if (revised.IsFailure)
            return Result<ObservationRequestAcceptance>.Failure(revised.Error!);

        var now = clock.UtcNow;

        var session = Session.CreateObservation(
            observationRequest.CoachId, observationRequest.AthleteProfileId,
            observationRequest.RequestedStartUtc, observationRequest.RequestedEndUtc,
            observationRequest.Location, request.DeductSession!.Value, now);

        db.Sessions.Add(session);

        var accepted = observationRequest.Accept(session.Id, actorUserId, now);

        if (accepted.IsFailure)
            return Result<ObservationRequestAcceptance>.Failure(accepted.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two acceptances got past the lock - only possible if the row was read outside it.
            // The transaction rolls back whole, so no session was created and the request is
            // whatever the winner made it.
            await transaction.RollbackAsync(ct);
            return Result<ObservationRequestAcceptance>.Failure(SchedulingErrors.ObservationRequestNotPending);
        }

        await audit.WriteAsync(
            "ObservationRequestAccepted",
            actorUserId,
            $"observationRequest={observationRequest.Id} session={session.Id} " +
            $"athleteProfile={observationRequest.AthleteProfileId} deductSession={request.DeductSession!.Value}",
            ct);

        await transaction.CommitAsync(ct);

        return Result<ObservationRequestAcceptance>.Success(
            new ObservationRequestAcceptance(observationRequest, session));
    }
}

/// <summary>Which of the two "no session" endings is being recorded.</summary>
public enum ObservationRequestDecision { Cancelled, Declined }

/// <summary>The request and the session it produced, as they both stand after one transaction.</summary>
public sealed record ObservationRequestAcceptance(ObservationRequest Request, Session Session);
