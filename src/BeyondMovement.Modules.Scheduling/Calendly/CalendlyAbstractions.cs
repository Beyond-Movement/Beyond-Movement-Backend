using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.Modules.Scheduling.Calendly;

public sealed record CalendlyAccount(string UserUri, string OrganizationUri, string Name);
public sealed record CalendlyLocationOption(string Kind, string? Location);
public sealed record CalendlyEventType(string Uri, string Name, int DurationMinutes, bool Active,
    string SchedulingUri, IReadOnlyList<CalendlyLocationOption> Locations);
public sealed record CalendlySlot(DateTime StartUtc, string SchedulingUrl);
public sealed record CalendlyInvitee(string Uri, string EventUri, string EventTypeUri, string Email,
    string Name, DateTime StartUtc, DateTime EndUtc, string Status, string? Location,
    string? MeetingUrl, string? CancelUrl, string? RescheduleUrl, bool Rescheduled,
    string? OldInviteeUri, string? NewInviteeUri);
public sealed record CreateCalendlyInvitee(string EventTypeUri, DateTime StartUtc, string Name,
    string Email, string TimeZone, string? LocationKind, string? Location);

public interface ICalendlyClient
{
    Task<CalendlyAccount> GetCurrentUserAsync(CancellationToken ct);
    Task<IReadOnlyList<CalendlyEventType>> GetEventTypesAsync(CancellationToken ct);
    Task<CalendlyEventType> GetEventTypeAsync(string eventTypeUri, CancellationToken ct);
    Task<IReadOnlyList<CalendlySlot>> GetAvailableTimesAsync(string eventTypeUri, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<CalendlyInvitee> CreateInviteeAsync(CreateCalendlyInvitee request, CancellationToken ct);
    Task CancelEventAsync(string eventUri, string? reason, CancellationToken ct);
    Task<IReadOnlyList<CalendlyInvitee>> GetScheduledInviteesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public interface ICalendlyWebhookVerifier
{
    bool IsValid(string payload, string? signatureHeader, DateTime nowUtc);
}

public interface ICalendlyWebhookParser
{
    CalendlyWebhookEnvelope Parse(string json);
}

public sealed record CalendlyWebhookEnvelope(string EventType, string IdempotencyKey,
    CalendlyInvitee Invitee, string? CancellationReason);

public enum CalendlyFailureKind { Unavailable, RateLimited, Unauthorized, Forbidden, NotFound, Validation, MalformedResponse }
public sealed class CalendlyApiException(CalendlyFailureKind kind, string message,
    int? retryAfterSeconds = null, Exception? inner = null) : Exception(message, inner)
{
    public CalendlyFailureKind Kind { get; } = kind;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
