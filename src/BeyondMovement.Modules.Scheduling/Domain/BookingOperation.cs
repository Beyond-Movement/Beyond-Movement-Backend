namespace BeyondMovement.Modules.Scheduling.Domain;

public sealed class BookingOperation
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AthleteProfileId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public Guid? SessionId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    private BookingOperation() { }
    public static BookingOperation Begin(Guid athleteProfileId, string key, DateTime nowUtc) => new()
    { AthleteProfileId = athleteProfileId, IdempotencyKey = key, CreatedAtUtc = nowUtc };
    public void Complete(Guid sessionId, DateTime nowUtc) { SessionId = sessionId; CompletedAtUtc = nowUtc; }
}
