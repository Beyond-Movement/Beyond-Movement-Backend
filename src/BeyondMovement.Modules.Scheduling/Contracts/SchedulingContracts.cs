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
public sealed record SessionResponse(Guid Id, Guid AthleteProfileId, string AthleteName,
    DateTime StartUtc, DateTime EndUtc,
    int DurationMinutes, DeliveryType DeliveryType, SessionStatus Status, string? LocationOrPlatform,
    string? MeetingUrl, string? RescheduleUrl);
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
        x.LocationOrPlatform, x.MeetingUrl, x.RescheduleUrl);
}

/// <summary>
/// Records an Observation the coach carried out — watching an athlete compete or train.
/// Observations are arranged in person and never appear on a Calendly booking page, so unlike
/// every other session this API stores, one is created here rather than projected from Calendly
/// (architecture A-03). The same Mark as Attended action then deducts it, subject to BR-07.
/// </summary>
/// <param name="AthleteProfileId">Whose observation it was. Admin-only, so it is named explicitly.</param>
/// <param name="LocationOrPlatform">Where it happened — the UI calls this "relevant location or event details".</param>
public sealed record CreateObservationRequest(
    Guid AthleteProfileId,
    DateTime StartUtc,
    DateTime EndUtc,
    string? LocationOrPlatform = null);

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
