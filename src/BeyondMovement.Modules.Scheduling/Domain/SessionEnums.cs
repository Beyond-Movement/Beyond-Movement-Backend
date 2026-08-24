namespace BeyondMovement.Modules.Scheduling.Domain;

public enum DeliveryType { Online, FaceToFace, Observation }
public enum SessionStatus { Scheduled, Cancelled }
public enum WebhookProcessingStatus { Pending, Processing, Processed, Failed }
public enum SchedulingChangeType { Booked, Cancelled, Rescheduled }
