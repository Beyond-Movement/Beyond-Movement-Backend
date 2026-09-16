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

/// <summary>
/// The life of an athlete's request for an observation. Stored as a string, like every other
/// enum in this database, so the order here is presentation only.
/// <para>
/// <b>Pending is the only state anything may be done from.</b> The other three are terminal and
/// read-only: an accepted request has already produced its session and any later change of date
/// belongs to that session, and a declined or cancelled one never will.
/// </para>
/// </summary>
public enum ObservationRequestStatus { Pending, Accepted, Declined, Cancelled }
