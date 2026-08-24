namespace BeyondMovement.Modules.Scheduling.Domain;

public enum DeliveryType { Online, FaceToFace, Observation }
/// <summary>
/// Stored as a string, so the order here is presentation only and adding a value is not a
/// migration hazard. All four the specification requires (architecture C-03): No-show is
/// Admin-only and, by default, deducts nothing (A-04).
/// </summary>
public enum SessionStatus { Scheduled, Attended, Cancelled, NoShow }
public enum WebhookProcessingStatus { Pending, Processing, Processed, Failed }
public enum SchedulingChangeType { Booked, Cancelled, Rescheduled }
