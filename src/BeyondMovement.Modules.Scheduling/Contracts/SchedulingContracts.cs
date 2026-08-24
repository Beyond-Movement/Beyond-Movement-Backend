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
public sealed record SessionResponse(Guid Id, Guid AthleteProfileId, DateTime StartUtc, DateTime EndUtc,
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
    public static SessionResponse ToResponse(this Session x) => new(x.Id, x.AthleteProfileId,
        x.ScheduledStartUtc, x.ScheduledEndUtc, x.DurationMinutes, x.DeliveryType, x.Status,
        x.LocationOrPlatform, x.MeetingUrl, x.RescheduleUrl);
}
