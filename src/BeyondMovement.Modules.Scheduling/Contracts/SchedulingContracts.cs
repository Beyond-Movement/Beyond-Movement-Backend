using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.Modules.Scheduling.Contracts;

public sealed record BookableLocation(string Kind, string? Location);
public sealed record BookableSessionType(string Id, string Name, int DurationMinutes,
    DeliveryType DeliveryType, IReadOnlyList<BookableLocation> Locations);
public sealed record AvailableSlot(DateTime StartUtc, DateTime EndUtc);
public sealed record BookSessionRequest(string EventTypeId, DateTime StartUtc, string TimeZone,
    string? LocationKind, string? Location);
public sealed record CancelSessionRequest(string? Reason);
public sealed record SessionResponse(Guid Id, Guid AthleteProfileId, DateTime StartUtc, DateTime EndUtc,
    int DurationMinutes, DeliveryType DeliveryType, SessionStatus Status, string? LocationOrPlatform,
    string? MeetingUrl, string? RescheduleUrl);
public sealed record SessionPage(IReadOnlyList<SessionResponse> Items, string? NextCursor);

public static class SessionMapping
{
    public static SessionResponse ToResponse(this Session x) => new(x.Id, x.AthleteProfileId,
        x.ScheduledStartUtc, x.ScheduledEndUtc, x.DurationMinutes, x.DeliveryType, x.Status,
        x.LocationOrPlatform, x.MeetingUrl, x.RescheduleUrl);
}
