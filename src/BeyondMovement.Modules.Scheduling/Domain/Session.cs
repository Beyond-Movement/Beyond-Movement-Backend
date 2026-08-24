namespace BeyondMovement.Modules.Scheduling.Domain;

public sealed class Session
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }
    public Guid AthleteProfileId { get; private set; }
    public Guid? PackageId { get; private set; }
    public string CalendlyEventUri { get; private set; } = null!;
    public string CalendlyInviteeUri { get; private set; } = null!;
    public string CalendlyEventTypeUri { get; private set; } = null!;
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

    public SchedulingChangeType Synchronize(CalendlySessionData data, DateTime nowUtc)
    {
        var changedTime = ScheduledStartUtc != data.StartUtc || ScheduledEndUtc != data.EndUtc;
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
        Status = SessionStatus.Scheduled;
        CancelledAtUtc = null;
        CancellationReason = null;
        UpdatedAtUtc = nowUtc;
        return changedTime ? SchedulingChangeType.Rescheduled : SchedulingChangeType.Booked;
    }

    public bool Cancel(DateTime nowUtc, string? reason)
    {
        if (Status == SessionStatus.Cancelled) return false;
        Status = SessionStatus.Cancelled;
        CancelledAtUtc = nowUtc;
        CancellationReason = reason;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value : value.ToUniversalTime();
}

public sealed record CalendlySessionData(
    string EventUri, string InviteeUri, string EventTypeUri, DateTime StartUtc, DateTime EndUtc,
    DeliveryType DeliveryType, string? LocationOrPlatform, string? MeetingUrl,
    string? CancelUrl, string? RescheduleUrl);
