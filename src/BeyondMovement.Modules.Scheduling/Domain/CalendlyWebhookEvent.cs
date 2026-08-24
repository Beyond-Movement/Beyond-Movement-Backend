namespace BeyondMovement.Modules.Scheduling.Domain;

public sealed class CalendlyWebhookEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string IdempotencyKey { get; private set; } = null!;
    public string EventType { get; private set; } = null!;
    public string PayloadJson { get; private set; } = null!;
    public WebhookProcessingStatus Status { get; private set; } = WebhookProcessingStatus.Pending;
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }

    private CalendlyWebhookEvent() { }
    public static CalendlyWebhookEvent Receive(string key, string type, string payload, DateTime nowUtc) => new()
    { IdempotencyKey = key, EventType = type, PayloadJson = payload, ReceivedAtUtc = nowUtc };
    public void Begin() { Status = WebhookProcessingStatus.Processing; Attempts++; }
    public void Complete(DateTime nowUtc) { Status = WebhookProcessingStatus.Processed; ProcessedAtUtc = nowUtc; LastError = null; }
    public void Fail(string error) { Status = WebhookProcessingStatus.Failed; LastError = error[..Math.Min(error.Length, 1000)]; }
}
