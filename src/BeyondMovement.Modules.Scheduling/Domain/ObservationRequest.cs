using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Scheduling.Domain;

/// <summary>
/// An athlete asking their coach to observe them — a competition, a training session, whatever
/// they would like watched. The athlete proposes a time, a place and some context; the Admin
/// reviews it, adjusts anything they need to, and accepts or declines.
/// <para>
/// This is a <b>request, not a session.</b> Nothing appears on the schedule and nothing can be
/// attended until the Admin accepts, at which point a real <see cref="Session"/> with
/// <see cref="DeliveryType.Observation"/> is created and linked through <see cref="SessionId"/>.
/// The two are written in one transaction, so an accepted request without its session cannot be
/// observed.
/// </para>
/// <para>
/// The shape is deliberately the one <c>PackagePurchase</c> already established: a Pending row
/// the athlete may revise, a single Admin transition that brings the real thing into existence,
/// a nullable id recording what it produced, and a row version behind both. Two request-shaped
/// workflows that behave differently is how a client ends up handling only one of them.
/// </para>
/// </summary>
public sealed class ObservationRequest
{
    /// <summary>Matches <c>Session.LocationOrPlatform</c>, so a location that validates here always fits there.</summary>
    public const int MaxLocationLength = 500;

    public const int MaxDetailsLength = 1000;

    /// <summary>
    /// What the athlete is taken to be asking for when nothing says otherwise. Their form
    /// collects a date, a time, a place and some context — not a duration — so one is supplied
    /// here rather than left null: a Pending request with no end cannot be drawn as a block on
    /// the Admin's schedule, and the Admin can change it when they accept.
    /// </summary>
    public const int DefaultDurationMinutes = 60;

    public const int MinDurationMinutes = 15;

    /// <summary>
    /// A day, the same bound <c>CreateObservationValidator</c> applies. An observation is a
    /// competition or a training session, not a training camp.
    /// </summary>
    public const int MaxDurationMinutes = 24 * 60;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }
    public Guid AthleteProfileId { get; private set; }

    /// <summary>
    /// When the athlete would like to be observed, as a UTC instant. The app converts from the
    /// athlete's local date and time before sending, exactly as it does when booking.
    /// </summary>
    public DateTime RequestedStartUtc { get; private set; }

    public int RequestedDurationMinutes { get; private set; }

    /// <summary>Where it is — the UI calls this "relevant location or event details".</summary>
    public string Location { get; private set; } = null!;

    /// <summary>
    /// Free text for the coach: what the athlete is training or competing in, and what they
    /// would like watched. Optional — an athlete may simply want their coach there.
    /// </summary>
    public string? Details { get; private set; }

    public ObservationRequestStatus Status { get; private set; } = ObservationRequestStatus.Pending;

    /// <summary>
    /// The session this request produced. Null until accepted, and set exactly once. A filtered
    /// unique index means even a bug in the handler cannot point two requests at one session.
    /// </summary>
    public Guid? SessionId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// When this request stopped being Pending, whichever way it went. One pair of fields for
    /// all three terminal states rather than three pairs: the question a support conversation
    /// asks is when it stopped waiting and who ended it, and the status says which of the three
    /// happened.
    /// </summary>
    public DateTime? ResolvedAtUtc { get; private set; }

    /// <summary>The Admin who accepted or declined, or the athlete who cancelled.</summary>
    public Guid? ResolvedByUserId { get; private set; }

    /// <summary>Maps to Postgres' <c>xmin</c>, as <c>Session</c> and <c>PackagePurchase</c> do.</summary>
    public uint Version { get; private set; }

    private ObservationRequest() { }   // EF Core

    public static ObservationRequest Create(
        Guid coachId, Guid athleteProfileId, DateTime startUtc, int durationMinutes,
        string location, string? details, DateTime nowUtc) => new()
        {
            CoachId = coachId,
            AthleteProfileId = athleteProfileId,
            RequestedStartUtc = EnsureUtc(startUtc),
            RequestedDurationMinutes = durationMinutes,
            Location = location.Trim(),
            Details = Normalize(details),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

    /// <summary>
    /// The athlete changes their mind while the coach has not answered yet, or the Admin adjusts
    /// the proposal before accepting it. One method for both, because they set the same four
    /// values and a second one could only drift from this.
    /// <para>
    /// A <b>full replacement</b>, not a patch: every field is written every time, matching the
    /// profile and purchase endpoints. Once the request has left Pending it is the record of
    /// what was agreed or refused and nobody may edit it — a later change of date belongs to the
    /// session the acceptance created, through the ordinary rescheduling behaviour.
    /// </para>
    /// </summary>
    public Result Revise(
        DateTime startUtc, int durationMinutes, string location, string? details, DateTime nowUtc)
    {
        if (Status != ObservationRequestStatus.Pending)
            return Result.Failure(SchedulingErrors.ObservationRequestNotPending);

        RequestedStartUtc = EnsureUtc(startUtc);
        RequestedDurationMinutes = durationMinutes;
        Location = location.Trim();
        Details = Normalize(details);
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>The athlete withdraws the request. No session is created, and none ever will be.</summary>
    public Result Cancel(Guid byUserId, DateTime nowUtc) =>
        Resolve(ObservationRequestStatus.Cancelled, byUserId, nowUtc);

    /// <summary>The Admin refuses. No session is created, and none ever will be.</summary>
    public Result Decline(Guid byUserId, DateTime nowUtc) =>
        Resolve(ObservationRequestStatus.Declined, byUserId, nowUtc);

    /// <summary>
    /// The Admin agrees, and the observation the athlete asked for becomes a real session.
    /// <para>
    /// <paramref name="sessionId"/> is passed in rather than created here: the caller makes the
    /// session and links it inside one transaction, so the id written on this row is provably the
    /// id of a session that exists. Refusing every non-Pending status is what makes the creation
    /// happen at most once — a second accept finds the request already Accepted and never reaches
    /// <c>Session.CreateObservation</c>. The <see cref="Version"/> row check and the row lock the
    /// service takes cover the case where both requests get past this test at the same instant.
    /// </para>
    /// </summary>
    public Result Accept(Guid sessionId, Guid byUserId, DateTime nowUtc)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sessionId), "An accepted request needs the session it produced.");

        var resolved = Resolve(ObservationRequestStatus.Accepted, byUserId, nowUtc);
        if (resolved.IsFailure) return resolved;

        SessionId = sessionId;
        return Result.Success();
    }

    /// <summary>
    /// The end of the session this request would produce. Stated here rather than computed at
    /// each call site, so the block the Admin sees and the session that gets created cannot
    /// disagree about how long it runs.
    /// </summary>
    public DateTime RequestedEndUtc => RequestedStartUtc.AddMinutes(RequestedDurationMinutes);

    private Result Resolve(ObservationRequestStatus outcome, Guid byUserId, DateTime nowUtc)
    {
        if (Status != ObservationRequestStatus.Pending)
            return Result.Failure(SchedulingErrors.ObservationRequestNotPending);

        Status = outcome;
        ResolvedByUserId = byUserId;
        ResolvedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// Null and an empty string both mean the athlete wrote nothing, and both store null, so a
    /// cleared box reads back as null rather than as a blank line the app would render.
    /// </summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value : value.ToUniversalTime();
}
