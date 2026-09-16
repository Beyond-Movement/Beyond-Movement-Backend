using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.Modules.Scheduling.Contracts;

public sealed record BookableLocation(string Kind, string? Location);
public sealed record BookableSessionType(string Id, string Name, int DurationMinutes,
    DeliveryType DeliveryType, IReadOnlyList<BookableLocation> Locations);
public sealed record AvailableSlot(DateTime StartUtc, DateTime EndUtc);

// LocationKind and Location default to null so the generated contract records them as
// optional. They are only ever required when the session type offers a choice, which is a
// run-time fact about the Calendly event type and not something the schema can state.
public sealed record BookSessionRequest(string EventTypeId, DateTime StartUtc, string TimeZone,
    string? LocationKind = null, string? Location = null);
public sealed record CancelSessionRequest(string? Reason);
// AthleteName is passed in rather than read from the session: the athlete's name belongs to
// Identity, which this module cannot see, so the caller joins it. See SchedulingEndpoints.
/// <param name="ObservationDeductsSession">
/// Whether marking this session Attended will consume a package session — the Admin's explicit
/// choice, made when the observation was recorded (BR-07). A boolean on every Observation, and
/// <c>null</c> on Online and FaceToFace sessions, which follow BR-05 and have no such choice to
/// report. The schema cannot state that condition, so it is stated here: a null on an Observation
/// is a contract violation, not a "no".
/// </param>
public sealed record SessionResponse(Guid Id, Guid AthleteProfileId, string AthleteName,
    DateTime StartUtc, DateTime EndUtc,
    int DurationMinutes, DeliveryType DeliveryType, SessionStatus Status, string? LocationOrPlatform,
    string? MeetingUrl, string? RescheduleUrl, bool? ObservationDeductsSession);
public sealed record SessionPage(IReadOnlyList<SessionResponse> Items, string? NextCursor);

/// <summary>
/// The reschedule link for one session. A single-field object rather than a bare string so the
/// response stays a JSON object the client can add to later without a breaking change.
/// </summary>
public sealed record RescheduleUrlResponse(string Url);

public static class SessionMapping
{
    public static SessionResponse ToResponse(this Session x, string athleteName) => new(x.Id,
        x.AthleteProfileId, athleteName,
        x.ScheduledStartUtc, x.ScheduledEndUtc, x.DurationMinutes, x.DeliveryType, x.Status,
        x.LocationOrPlatform, x.MeetingUrl, x.RescheduleUrl, x.ObservationDeductsSession);
}

/// <summary>
/// Records an Observation the coach carried out — watching an athlete compete or train.
/// Observations are arranged in person and never appear on a Calendly booking page, so unlike
/// every other session this API stores, one is created here rather than projected from Calendly
/// (architecture A-03). The same Mark as Attended action then deducts it, subject to BR-07.
/// <para>
/// The dates may be in the past or the future: an observation is arranged directly with the
/// athlete, so the Admin may record one they have just carried out or one they have agreed to
/// attend next week.
/// </para>
/// </summary>
/// <param name="AthleteProfileId">Whose observation it was. Admin-only, so it is named explicitly.</param>
/// <param name="DeductSession">
/// Whether attending this observation should consume one package session (BR-07). Required, and
/// deliberately not defaulted: the whole point of the field is that a human decided, rather than
/// the server inferring it from how long the observation ran. Nothing is deducted now — the
/// choice is stored and applied when the observation is marked Attended.
/// </param>
/// <param name="LocationOrPlatform">Where it happened — the UI calls this "relevant location or event details".</param>
public sealed record CreateObservationRequest(
    Guid AthleteProfileId,
    DateTime StartUtc,
    DateTime EndUtc,
    // Nullable so that an omitted field and an explicit null are both rejected rather than
    // silently becoming false, which is the failure this field exists to prevent. No default, so
    // the generated schema lists it as required.
    bool? DeductSession,
    string? LocationOrPlatform = null);

/// <summary>
/// What the athlete fills in on Request an Observation, and what the Admin sends when they
/// adjust the proposal before accepting it. One shape for both, because they set the same
/// values — see <see cref="ObservationRequest.Revise"/>.
/// <para>
/// A <b>full replacement</b>, not a patch: send every field every time, and a field left out is
/// one being cleared rather than one being left alone. The same rule the profile and purchase
/// endpoints follow.
/// </para>
/// </summary>
/// <param name="RequestedStartUtc">
/// When the athlete would like to be observed. Must be UTC and in the future — the app converts
/// the local date and time it collected, exactly as it does when booking a session.
/// </param>
/// <param name="RequestedDurationMinutes">
/// How long it runs. Optional, and <c>null</c> means
/// <see cref="ObservationRequest.DefaultDurationMinutes"/>: the athlete's form does not ask, so
/// the request carries a sensible proposal the Admin can change rather than an absent one that
/// cannot be drawn on a schedule.
/// </param>
/// <param name="Details">
/// Optional context for the coach — what is being trained or competed in, and what the athlete
/// would like watched. Null and an empty string both clear it and both read back as null.
/// </param>
public sealed record SaveObservationRequestRequest(
    DateTime RequestedStartUtc,
    string Location,
    string? Details = null,
    int? RequestedDurationMinutes = null);

/// <summary>
/// The Admin's decision. Everything but <paramref name="DeductSession"/> is an override of what
/// the athlete asked for, and <c>null</c> on any of them means "accept it as requested" — so an
/// Admin who changes nothing sends only the one field they must.
/// </summary>
/// <param name="DeductSession">
/// BR-07, and the reason this cannot be a bare accept. Whether attending the observation will
/// consume one package session is the Admin's explicit choice, made when the session is
/// recorded; an athlete can never make it, so it does not appear on the request and has to be
/// answered here. Nullable with no default so that an omitted field and an explicit null are
/// both rejected rather than silently becoming false, exactly as on
/// <see cref="CreateObservationRequest"/>.
/// <para>
/// Nothing is deducted by accepting. The session is created Scheduled, a booking never deducts
/// (BR-04), and the choice is applied later at Mark as Attended.
/// </para>
/// </param>
public sealed record AcceptObservationRequestRequest(
    bool? DeductSession,
    DateTime? RequestedStartUtc = null,
    string? Location = null,
    string? Details = null,
    int? RequestedDurationMinutes = null);

/// <param name="AthleteUserId">
/// The athlete's <b>user</b> id — the id <c>GET /athletes/{athleteId}</c> takes — so the Admin's
/// queue can open a profile without a lookup per row. Not the profile id that sessions are
/// keyed by, which is <paramref name="AthleteProfileId"/>.
/// </param>
/// <param name="AthleteName">
/// Carried on the row for the same reason <c>SessionResponse.athleteName</c> is: an Admin queue
/// should draw without a call per card. The athlete's full name, or their email address if they
/// have not completed their profile yet.
/// </param>
/// <param name="SessionId">
/// The observation this request produced. Non-null exactly when <paramref name="Status"/> is
/// Accepted; read it to move on to <c>GET /sessions/{id}</c>, which is where every later change
/// of date or cancellation happens.
/// </param>
/// <param name="ResolvedAtUtc">
/// When the request stopped being Pending, whichever way it went. Null while it is still
/// waiting, and non-null on all three terminal states.
/// </param>
public sealed record ObservationRequestResponse(
    Guid Id,
    Guid AthleteProfileId,
    Guid AthleteUserId,
    string AthleteName,
    DateTime RequestedStartUtc,
    DateTime RequestedEndUtc,
    int RequestedDurationMinutes,
    string Location,
    string? Details,
    ObservationRequestStatus Status,
    Guid? SessionId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ResolvedAtUtc);

/// <summary>
/// Both halves of an acceptance, so the app can replace its copy of the request and add the new
/// session without re-reading either. They are written in one transaction, so what is returned
/// here is what exists.
/// </summary>
public sealed record AcceptObservationRequestResponse(
    ObservationRequestResponse Request,
    SessionResponse Session);

public static class ObservationRequestMapping
{
    public static ObservationRequestResponse ToResponse(
        this ObservationRequest x, Guid athleteUserId, string athleteName) => new(
        x.Id, x.AthleteProfileId, athleteUserId, athleteName,
        x.RequestedStartUtc, x.RequestedEndUtc, x.RequestedDurationMinutes,
        x.Location, x.Details, x.Status, x.SessionId,
        x.CreatedAtUtc, x.UpdatedAtUtc, x.ResolvedAtUtc);
}

public sealed record SaveSessionNoteRequest(string Content);

public sealed record SessionNoteResponse(
    Guid Id,
    Guid SessionId,
    Guid AuthorUserId,
    string Content,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class SessionNoteMapping
{
    public static SessionNoteResponse ToResponse(this SessionNote x) =>
        new(x.Id, x.SessionId, x.AuthorUserId, x.Content, x.CreatedAtUtc, x.UpdatedAtUtc);
}
