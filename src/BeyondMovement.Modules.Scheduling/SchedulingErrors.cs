using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Scheduling;

public static class SchedulingErrors
{
    public static readonly string[] AllCodes =
    [
        "AVAILABILITY_RANGE_INVALID", "BOOKING_IN_PROGRESS", "CALENDLY_RATE_LIMITED",
        "CALENDLY_SIGNATURE_INVALID", "CALENDLY_UNAVAILABLE", "DUPLICATE_BOOKING",
        "EVENT_TYPE_INVALID", "IDEMPOTENCY_KEY_REQUIRED", "LOCATION_INVALID",
        "LOCATION_REQUIRED", "SESSION_NOT_FOUND", "SLOT_UNAVAILABLE", "TIME_ZONE_INVALID"
    ];
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
    public static Error CalendlyRateLimited(int? retryAfter) => new("CALENDLY_RATE_LIMITED", "Scheduling is temporarily busy. Try again shortly.", 503, retryAfter);
}
