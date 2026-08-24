using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Scheduling;

public static class SchedulingErrors
{
    public static readonly string[] AllCodes =
    [
        "AVAILABILITY_RANGE_INVALID", "BOOKING_IN_PROGRESS", "CALENDLY_RATE_LIMITED",
        "CALENDLY_SIGNATURE_INVALID", "CALENDLY_UNAVAILABLE", "DUPLICATE_BOOKING",
        "EVENT_TYPE_INVALID", "IDEMPOTENCY_KEY_REQUIRED", "LOCATION_INVALID",
        "LOCATION_REQUIRED", "SESSION_NOT_FOUND", "SLOT_UNAVAILABLE", "TIME_ZONE_INVALID",
        "SESSION_ALREADY_ATTENDED", "SESSION_ALREADY_RESOLVED", "SESSION_CANCELLED",
        "SESSION_NOTE_NOT_FOUND", "OBSERVATION_RANGE_INVALID"
    ];
    /// <summary>
    /// Calendly's event_type_available_times refuses a window wider than seven days, so the same
    /// bound is enforced before the call rather than after. Passing a wider range straight through
    /// comes back as a Calendly 400, which maps to SLOT_UNAVAILABLE — telling an athlete a time is
    /// taken when what actually happened is that the app asked for too much at once.
    /// </summary>
    public const int MaxAvailabilityDays = 7;

    public static readonly Error AvailabilityRangeInvalid = new("AVAILABILITY_RANGE_INVALID",
        $"Use a future UTC range of at most {MaxAvailabilityDays} days.", 400);
    public static readonly Error CalendlyUnavailable = new("CALENDLY_UNAVAILABLE", "Scheduling is temporarily unavailable.", 503);
    public static readonly Error EventTypeInvalid = new("EVENT_TYPE_INVALID", "This session type is unavailable.", 404);
    public static readonly Error SlotUnavailable = new("SLOT_UNAVAILABLE", "That time is no longer available.", 409);
    public static readonly Error SessionNotFound = new("SESSION_NOT_FOUND", "Session not found.", 404);
    public static readonly Error DuplicateBooking = new("DUPLICATE_BOOKING", "This booking request was already processed.", 409);
    public static readonly Error BookingInProgress = new("BOOKING_IN_PROGRESS", "This booking request is still being processed.", 409, 5);
    public static readonly Error IdempotencyKeyRequired = new("IDEMPOTENCY_KEY_REQUIRED", "An Idempotency-Key header is required.", 400);
    public static readonly Error TimeZoneInvalid = new("TIME_ZONE_INVALID", "Use a valid IANA time zone.", 400);
    public static readonly Error LocationRequired = new("LOCATION_REQUIRED", "A location selection is required for this session type.", 400);
    public static readonly Error LocationInvalid = new("LOCATION_INVALID", "That location is not available for this session type.", 400);
    public static readonly Error CalendlySignatureInvalid = new("CALENDLY_SIGNATURE_INVALID", "Webhook signature is invalid.", 401);
    /// <summary>
    /// The session has already been marked Attended. A 409 rather than a quiet 200, because the
    /// Admin who taps twice is usually reacting to a screen that did not refresh, and telling
    /// them so is more useful than pretending the second tap did the work. The deduction is
    /// unaffected either way — this is the check that makes it happen exactly once.
    /// </summary>
    public static readonly Error SessionAlreadyAttended = new("SESSION_ALREADY_ATTENDED",
        "This session has already been marked attended.", 409);

    public static readonly Error SessionAlreadyResolved = new("SESSION_ALREADY_RESOLVED",
        "This session has already been marked a no-show.", 409);

    /// <summary>BR-06 — a cancelled session never consumes one, so it cannot be attended either.</summary>
    public static readonly Error SessionCancelled = new("SESSION_CANCELLED",
        "This session was cancelled and cannot be marked attended.", 409);

    public static readonly Error SessionNoteNotFound = new("SESSION_NOTE_NOT_FOUND",
        "Session note not found.", 404);

    /// <summary>
    /// Observations are recorded after they happen, so the range must be in the past as well as
    /// in order. A future observation is almost always a typo in the date.
    /// </summary>
    public static readonly Error ObservationRangeInvalid = new("OBSERVATION_RANGE_INVALID",
        "Use a UTC range that is in order, in the past, and no longer than a day.", 400);

    public static Error CalendlyRateLimited(int? retryAfter) => new("CALENDLY_RATE_LIMITED", "Scheduling is temporarily busy. Try again shortly.", 503, retryAfter);
}
