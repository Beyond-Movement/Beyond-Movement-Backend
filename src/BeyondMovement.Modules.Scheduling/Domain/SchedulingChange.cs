namespace BeyondMovement.Modules.Scheduling.Domain;

public sealed class SchedulingChange
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }
    public SchedulingChangeType Type { get; private set; }
    public string DedupKey { get; private set; } = null!;
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    private SchedulingChange() { }
    public static SchedulingChange Record(Guid sessionId, SchedulingChangeType type, string providerIdentity, DateTime nowUtc) => new()
    { SessionId = sessionId, Type = type, DedupKey = $"session:{sessionId}:{type}:{providerIdentity}", OccurredAtUtc = nowUtc };
    public void MarkPublished(DateTime nowUtc) => PublishedAtUtc = nowUtc;
}

public sealed class CalendlyUnmatchedBooking
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string CalendlyEventUri { get; private set; } = null!;
    public string CalendlyInviteeUri { get; private set; } = null!;
    public string CalendlyEventTypeUri { get; private set; } = null!;
    public string InviteeEmail { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public DateTime DiscoveredAtUtc { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }
    public Guid? AssignedAthleteProfileId { get; private set; }
    private CalendlyUnmatchedBooking() { }
    public static CalendlyUnmatchedBooking Record(string eventUri, string inviteeUri, string eventTypeUri,
        string email, string reason, DateTime nowUtc) => new()
    { CalendlyEventUri = eventUri, CalendlyInviteeUri = inviteeUri, CalendlyEventTypeUri = eventTypeUri,
      InviteeEmail = email.ToLowerInvariant(), Reason = reason, DiscoveredAtUtc = nowUtc };
    public void Resolve(Guid athleteProfileId, DateTime nowUtc) { AssignedAthleteProfileId = athleteProfileId; ResolvedAtUtc = nowUtc; }
}

public sealed class CalendlyReconciliationRun
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int RemoteCount { get; private set; }
    public int CreatedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int CancelledCount { get; private set; }
    public int FlaggedCount { get; private set; }
    public string? Error { get; private set; }
    private CalendlyReconciliationRun() { }
    public static CalendlyReconciliationRun Start(DateTime nowUtc) => new() { StartedAtUtc = nowUtc };
    public void Complete(DateTime nowUtc, int remote, int created, int updated, int cancelled, int flagged)
    { CompletedAtUtc = nowUtc; RemoteCount = remote; CreatedCount = created; UpdatedCount = updated; CancelledCount = cancelled; FlaggedCount = flagged; }
    public void Fail(DateTime nowUtc, string error) { CompletedAtUtc = nowUtc; Error = error[..Math.Min(error.Length, 1000)]; }
}
