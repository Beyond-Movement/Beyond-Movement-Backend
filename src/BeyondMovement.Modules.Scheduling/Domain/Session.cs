using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Scheduling.Domain;

public sealed class Session
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }
    public Guid AthleteProfileId { get; private set; }
    public Guid? PackageId { get; private set; }
    /// <summary>
    /// Null for a session this API created itself. Only Observations are created that way
    /// (A-03): everything else originates in Calendly and carries all three identifiers. The
    /// unique indexes still hold, because Postgres lets a unique index hold many nulls.
    /// </summary>
    public string? CalendlyEventUri { get; private set; }
    public string? CalendlyInviteeUri { get; private set; }
    public string? CalendlyEventTypeUri { get; private set; }
    public DateTime ScheduledStartUtc { get; private set; }
    public DateTime ScheduledEndUtc { get; private set; }
    public int DurationMinutes { get; private set; }
    public DeliveryType DeliveryType { get; private set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Scheduled;
    public string? LocationOrPlatform { get; private set; }
    public string? MeetingUrl { get; private set; }
    public string? CancelUrl { get; private set; }
    public string? RescheduleUrl { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Set only when <see cref="Status"/> is Attended. A no-show is evidenced by the audit log.</summary>
    public DateTime? AttendedAtUtc { get; private set; }
    public Guid? AttendedByUserId { get; private set; }

    /// <summary>
    /// What this session actually took off a package — <c>0</c> or <c>1</c>, and the anchor of
    /// exactly-once deduction. Because the session records what it consumed, the deduction is
    /// verifiable after the fact rather than being inferred from a balance, and a reversal knows
    /// exactly how much to give back. An observation the Admin chose not to deduct (BR-07) and a
    /// non-deducting no-show (A-04) both record <c>0</c>.
    /// </summary>
    public int ConsumedSessionCount { get; private set; }

    /// <summary>
    /// The Admin's explicit answer, given when the observation was recorded, to whether attending
    /// it consumes a package session (BR-07). Non-null exactly when <see cref="DeliveryType"/> is
    /// Observation, and null for everything Calendly books, which follows BR-05 instead.
    /// <para>
    /// Stored rather than derived, because the decision is made at creation and applied at Attend
    /// — and because the rule it replaced read <see cref="DurationMinutes"/>, a value a Calendly
    /// reschedule can move under a session. What the Admin chose has to outlive that.
    /// </para>
    /// </summary>
    public bool? ObservationDeductsSession { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public uint Version { get; private set; }

    private Session() { }

    public static Session Create(Guid coachId, Guid athleteProfileId, CalendlySessionData data, DateTime nowUtc) => new()
    {
        CoachId = coachId,
        AthleteProfileId = athleteProfileId,
        CalendlyEventUri = data.EventUri,
        CalendlyInviteeUri = data.InviteeUri,
        CalendlyEventTypeUri = data.EventTypeUri,
        ScheduledStartUtc = EnsureUtc(data.StartUtc),
        ScheduledEndUtc = EnsureUtc(data.EndUtc),
        DurationMinutes = checked((int)(data.EndUtc - data.StartUtc).TotalMinutes),
        DeliveryType = data.DeliveryType,
        LocationOrPlatform = data.LocationOrPlatform,
        MeetingUrl = data.MeetingUrl,
        CancelUrl = data.CancelUrl,
        RescheduleUrl = data.RescheduleUrl,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc
    };

    /// <summary>
    /// Brings this session back in line with Calendly. Attendance is deliberately untouched: a
    /// session that has already been marked Attended or No-show has consumed what it consumed,
    /// and a later Calendly edit arriving out of order must not quietly return it to Scheduled
    /// and strand the deduction (architecture section 12, the out-of-order cancellation case).
    /// </summary>
    public SchedulingChangeType Synchronize(CalendlySessionData data, DateTime nowUtc)
    {
        var changedTime = ScheduledStartUtc != data.StartUtc || ScheduledEndUtc != data.EndUtc;
        var resolved = Status is SessionStatus.Attended or SessionStatus.NoShow;
        CalendlyEventUri = data.EventUri;
        CalendlyInviteeUri = data.InviteeUri;
        CalendlyEventTypeUri = data.EventTypeUri;
        ScheduledStartUtc = EnsureUtc(data.StartUtc);
        ScheduledEndUtc = EnsureUtc(data.EndUtc);
        DurationMinutes = checked((int)(data.EndUtc - data.StartUtc).TotalMinutes);
        DeliveryType = data.DeliveryType;
        LocationOrPlatform = data.LocationOrPlatform;
        MeetingUrl = data.MeetingUrl;
        CancelUrl = data.CancelUrl;
        RescheduleUrl = data.RescheduleUrl;
        if (!resolved)
        {
            Status = SessionStatus.Scheduled;
            CancelledAtUtc = null;
            CancellationReason = null;
        }

        UpdatedAtUtc = nowUtc;
        return changedTime ? SchedulingChangeType.Rescheduled : SchedulingChangeType.Booked;
    }

    /// <summary>
    /// Returns whether anything changed, which is what tells the caller to raise a scheduling
    /// change. An already-cancelled session is a no-op so a repeated tap is safe.
    /// <para>
    /// A session that has been Attended or marked No-show is also a no-op. Cancelling it would
    /// erase the record of what it consumed while the package balance stayed decremented, and
    /// the platform never reverses consumed value silently (BR-06 governs cancellation *before*
    /// attendance, which is the ordinary case and does not touch the balance at all).
    /// </para>
    /// </summary>
    public bool Cancel(DateTime nowUtc, string? reason)
    {
        if (Status is SessionStatus.Cancelled or SessionStatus.Attended or SessionStatus.NoShow) return false;
        Status = SessionStatus.Cancelled;
        CancelledAtUtc = nowUtc;
        CancellationReason = reason;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// A session this API created rather than Calendly. Observations are the only such sessions
    /// (A-03): they are arranged in person and never appear on a booking page, so the Admin
    /// records one directly — for a date already past, or one still to come — and the same Mark
    /// as Attended action later applies <paramref name="deductsSession"/>.
    /// <para>
    /// Creating one deducts nothing whatever <paramref name="deductsSession"/> says: the session
    /// starts Scheduled, and a scheduled session has never taken anything (BR-04).
    /// </para>
    /// </summary>
    /// <param name="deductsSession">
    /// The Admin's explicit choice, recorded now and applied at Attend. Required — there is no
    /// default, because inferring one is exactly what BR-07 used to do.
    /// </param>
    public static Session CreateObservation(Guid coachId, Guid athleteProfileId, DateTime startUtc,
        DateTime endUtc, string? locationOrPlatform, bool deductsSession, DateTime nowUtc) => new()
        {
            CoachId = coachId,
            AthleteProfileId = athleteProfileId,
            ScheduledStartUtc = EnsureUtc(startUtc),
            ScheduledEndUtc = EnsureUtc(endUtc),
            DurationMinutes = checked((int)(EnsureUtc(endUtc) - EnsureUtc(startUtc)).TotalMinutes),
            DeliveryType = DeliveryType.Observation,
            LocationOrPlatform = locationOrPlatform,
            ObservationDeductsSession = deductsSession,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

    /// <summary>
    /// How many sessions this session takes off a package when resolved with
    /// <paramref name="outcome"/> — always <c>0</c> or <c>1</c>. The single place the three
    /// deduction rules meet, so no caller has to remember any of them:
    /// <list type="bullet">
    /// <item>An ordinary attended session consumes one (BR-05).</item>
    /// <item>An attended <b>observation</b> consumes one exactly when the Admin said it should
    /// when recording it (BR-07). Duration has nothing to do with it.</item>
    /// <item>A no-show consumes what the deployment's policy says, which defaults to nothing (A-04).</item>
    /// </list>
    /// Cancelled and scheduled sessions consume nothing — a booking never deducts (BR-04) and a
    /// cancellation never does either (BR-06).
    /// </summary>
    public int ConsumptionFor(SessionStatus outcome, bool noShowDeducts) => outcome switch
    {
        // == true, not ?? throw: the column is only null on a row that predates the choice, and
        // an observation whose decision was never recorded must not invent one and deduct.
        SessionStatus.Attended when DeliveryType == DeliveryType.Observation =>
            ObservationDeductsSession == true ? 1 : 0,
        SessionStatus.Attended => 1,
        SessionStatus.NoShow => noShowDeducts ? 1 : 0,
        _ => 0
    };

    /// <summary>
    /// Records the outcome of a session that has happened. <paramref name="consumedSessionCount"/>
    /// is passed in rather than recomputed here, so that the number written on the session is
    /// provably the same number taken off the package — the caller deducts exactly what it is
    /// about to record, inside one transaction.
    /// <para>
    /// Every non-Scheduled status is refused, which is what makes the deduction happen at most
    /// once: a second Mark as Attended finds the session already Attended and never reaches the
    /// package. The <see cref="Version"/> row check covers the case where both requests get past
    /// this test at the same instant.
    /// </para>
    /// </summary>
    public Result Resolve(SessionStatus outcome, int consumedSessionCount, Guid byUserId, DateTime nowUtc)
    {
        if (outcome is not (SessionStatus.Attended or SessionStatus.NoShow))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A session resolves to Attended or NoShow.");

        ArgumentOutOfRangeException.ThrowIfNegative(consumedSessionCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(consumedSessionCount, 1);

        switch (Status)
        {
            case SessionStatus.Attended: return Result.Failure(SchedulingErrors.SessionAlreadyAttended);
            case SessionStatus.NoShow: return Result.Failure(SchedulingErrors.SessionAlreadyResolved);
            case SessionStatus.Cancelled: return Result.Failure(SchedulingErrors.SessionCancelled);
        }

        Status = outcome;
        ConsumedSessionCount = consumedSessionCount;

        // Stamped for Attended only. A no-show did not attend anything, and a field called
        // AttendedAt holding the moment somebody did not turn up is the kind of small lie that
        // later gets read as attendance in a report. The audit log records who marked it.
        if (outcome == SessionStatus.Attended)
        {
            AttendedAtUtc = nowUtc;
            AttendedByUserId = byUserId;
        }

        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>Links this session to the package it consumed, for the audit trail.</summary>
    public void AttachToPackage(Guid? packageId) => PackageId = packageId;

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value : value.ToUniversalTime();
}

public sealed record CalendlySessionData(
    string EventUri, string InviteeUri, string EventTypeUri, DateTime StartUtc, DateTime EndUtc,
    DeliveryType DeliveryType, string? LocationOrPlatform, string? MeetingUrl,
    string? CancelUrl, string? RescheduleUrl);
