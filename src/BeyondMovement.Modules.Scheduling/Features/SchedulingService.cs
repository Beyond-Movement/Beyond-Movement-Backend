using BeyondMovement.Modules.Scheduling.Calendly;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Modules.Scheduling.Features;

public sealed class SchedulingService(
    ISchedulingDbContext db, ICalendlyClient calendly, IOptions<CalendlyOptions> options, IClock clock)
{
    private readonly CalendlyOptions _options = options.Value;

    public async Task<Result<IReadOnlyList<BookableSessionType>>> GetBookableTypesAsync(CancellationToken ct)
    {
        if (!_options.Configured) return Result<IReadOnlyList<BookableSessionType>>.Failure(SchedulingErrors.CalendlyUnavailable);
        try
        {
            var types = await calendly.GetEventTypesAsync(ct);
            return Result<IReadOnlyList<BookableSessionType>>.Success(types
                .Select(x => (Type: x, Mapping: _options.FindByUri(x.Uri)))
                .Where(x => x.Type.Active && x.Mapping is not null)
                .Select(x => new BookableSessionType(Id(x.Type.Uri), x.Type.Name, x.Type.DurationMinutes,
                    x.Mapping!.DeliveryType, x.Type.Locations.Select(l => new BookableLocation(l.Kind, l.Location)).ToArray()))
                .ToArray());
        }
        catch (CalendlyApiException ex) { return Result<IReadOnlyList<BookableSessionType>>.Failure(MapFailure(ex)); }
    }

    public async Task<Result<IReadOnlyList<AvailableSlot>>> GetAvailabilityAsync(
        string eventTypeId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var mapping = _options.FindById(eventTypeId);
        if (mapping is null) return Result<IReadOnlyList<AvailableSlot>>.Failure(SchedulingErrors.EventTypeInvalid);
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc < clock.UtcNow || fromUtc >= toUtc
            || toUtc - fromUtc > TimeSpan.FromDays(SchedulingErrors.MaxAvailabilityDays))
            return Result<IReadOnlyList<AvailableSlot>>.Failure(SchedulingErrors.AvailabilityRangeInvalid);
        try
        {
            var eventType = await calendly.GetEventTypeAsync(mapping.Uri, ct);
            var slots = await calendly.GetAvailableTimesAsync(mapping.Uri, fromUtc, toUtc, ct);
            return Result<IReadOnlyList<AvailableSlot>>.Success(slots
                .Select(x => new AvailableSlot(x.StartUtc, x.StartUtc.AddMinutes(eventType.DurationMinutes))).ToArray());
        }
        catch (CalendlyApiException ex) { return Result<IReadOnlyList<AvailableSlot>>.Failure(MapFailure(ex)); }
    }

    public async Task<Result<SessionResponse>> BookAsync(Guid coachId, Guid athleteProfileId,
        string athleteName, string athleteEmail, string idempotencyKey,
        BookSessionRequest request, CancellationToken ct)
    {
        var mapping = _options.FindById(request.EventTypeId);
        if (mapping is null) return Result<SessionResponse>.Failure(SchedulingErrors.EventTypeInvalid);
        if (request.StartUtc.Kind != DateTimeKind.Utc || request.StartUtc <= clock.UtcNow)
            return Result<SessionResponse>.Failure(SchedulingErrors.SlotUnavailable);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone); }
        catch (TimeZoneNotFoundException) { return Result<SessionResponse>.Failure(SchedulingErrors.TimeZoneInvalid); }
        catch (InvalidTimeZoneException) { return Result<SessionResponse>.Failure(SchedulingErrors.TimeZoneInvalid); }

        var key = idempotencyKey.Trim();
        var prior = await db.BookingOperations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AthleteProfileId == athleteProfileId && x.IdempotencyKey == key, ct);
        if (prior?.SessionId is { } priorSessionId)
        {
            var priorSession = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == priorSessionId, ct);
            if (priorSession is not null) return Result<SessionResponse>.Success(priorSession.ToResponse());
        }
        if (prior is not null) return Result<SessionResponse>.Failure(SchedulingErrors.BookingInProgress);

        try
        {
            var eventType = await calendly.GetEventTypeAsync(mapping.Uri, ct);
            var locationFailure = ValidateLocation(eventType, request);
            if (locationFailure is not null) return Result<SessionResponse>.Failure(locationFailure);
            var slots = await calendly.GetAvailableTimesAsync(mapping.Uri, request.StartUtc, request.StartUtc.AddMinutes(1), ct);
            if (!slots.Any(x => x.StartUtc == request.StartUtc))
                return Result<SessionResponse>.Failure(SchedulingErrors.SlotUnavailable);

            var operation = BookingOperation.Begin(athleteProfileId, key, clock.UtcNow);
            db.BookingOperations.Add(operation);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Result<SessionResponse>.Failure(SchedulingErrors.BookingInProgress); }

            var created = await calendly.CreateInviteeAsync(new(mapping.Uri, request.StartUtc, athleteName,
                athleteEmail, request.TimeZone, request.LocationKind, request.Location), ct);
            var data = Map(created, mapping.DeliveryType);
            var existing = await db.Sessions.FirstOrDefaultAsync(x =>
                x.CalendlyEventUri == created.EventUri || x.CalendlyInviteeUri == created.Uri, ct);
            if (existing is null)
            {
                existing = Session.Create(coachId, athleteProfileId, data, clock.UtcNow);
                db.Sessions.Add(existing);
            }
            else existing.Synchronize(data, clock.UtcNow);
            operation?.Complete(existing.Id, clock.UtcNow);
            await RecordChangeAsync(existing.Id, SchedulingChangeType.Booked, created.Uri, ct);
            await db.SaveChangesAsync(ct);
            return Result<SessionResponse>.Success(existing.ToResponse());
        }
        catch (CalendlyApiException ex)
        {
            var operation = await db.BookingOperations.SingleOrDefaultAsync(x =>
                x.AthleteProfileId == athleteProfileId && x.IdempotencyKey == key && x.SessionId == null, ct);
            if (operation is not null) { db.BookingOperations.Remove(operation); await db.SaveChangesAsync(ct); }
            return Result<SessionResponse>.Failure(MapFailure(ex));
        }
    }

    public async Task<Result> CancelAsync(Session session, string? reason, CancellationToken ct)
    {
        if (session.Status == SessionStatus.Cancelled) return Result.Success();
        try { await calendly.CancelEventAsync(session.CalendlyEventUri, reason, ct); }
        catch (CalendlyApiException ex) { return Result.Failure(MapFailure(ex)); }
        session.Cancel(clock.UtcNow, reason);
        await RecordChangeAsync(session.Id, SchedulingChangeType.Cancelled, session.CalendlyInviteeUri, ct);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<SchedulingChangeType> UpsertAsync(Guid coachId, Guid athleteProfileId,
        CalendlyInvitee invitee, CancellationToken ct)
    {
        var mapping = _options.FindByUri(invitee.EventTypeUri)
            ?? throw new UnmatchedCalendlyEventTypeException(invitee.EventTypeUri);
        var delivery = mapping.DeliveryType;
        var data = Map(invitee, delivery);
        var session = await db.Sessions.FirstOrDefaultAsync(x =>
            x.CalendlyInviteeUri == invitee.Uri || x.CalendlyEventUri == invitee.EventUri ||
            (invitee.OldInviteeUri != null && x.CalendlyInviteeUri == invitee.OldInviteeUri), ct);
        if (session is null)
        {
            session = Session.Create(coachId, athleteProfileId, data, clock.UtcNow);
            db.Sessions.Add(session);
            await RecordChangeAsync(session.Id, SchedulingChangeType.Booked, invitee.Uri, ct);
            await db.SaveChangesAsync(ct);
            return SchedulingChangeType.Booked;
        }
        var change = session.Synchronize(data, clock.UtcNow);
        await RecordChangeAsync(session.Id, change, invitee.Uri, ct);
        await db.SaveChangesAsync(ct);
        return change;
    }

    private static string Id(string uri) => uri.TrimEnd('/').Split('/').Last();
    private static CalendlySessionData Map(CalendlyInvitee x, DeliveryType delivery) => new(
        x.EventUri, x.Uri, x.EventTypeUri, x.StartUtc, x.EndUtc, delivery, x.Location,
        x.MeetingUrl, x.CancelUrl, x.RescheduleUrl);

    private static Error? ValidateLocation(CalendlyEventType type, BookSessionRequest request)
    {
        if (type.Locations.Count == 0)
            return request.LocationKind is null ? null : SchedulingErrors.LocationInvalid;
        if (string.IsNullOrWhiteSpace(request.LocationKind)) return SchedulingErrors.LocationRequired;
        var option = type.Locations.SingleOrDefault(x => string.Equals(x.Kind, request.LocationKind, StringComparison.OrdinalIgnoreCase));
        if (option is null) return SchedulingErrors.LocationInvalid;
        var needsValue = option.Kind is "ask_invitee" or "outbound_call" || type.Locations.Count > 1 && option.Location is null;
        return needsValue && string.IsNullOrWhiteSpace(request.Location) ? SchedulingErrors.LocationRequired : null;
    }

    private static Error MapFailure(CalendlyApiException ex) => ex.Kind switch
    {
        CalendlyFailureKind.NotFound or CalendlyFailureKind.Validation => SchedulingErrors.SlotUnavailable,
        CalendlyFailureKind.RateLimited => SchedulingErrors.CalendlyRateLimited(ex.RetryAfterSeconds),
        _ => SchedulingErrors.CalendlyUnavailable
    };

    private async Task RecordChangeAsync(Guid sessionId, SchedulingChangeType type, string providerIdentity, CancellationToken ct)
    {
        var key = $"session:{sessionId}:{type}:{providerIdentity}";
        if (!await db.SchedulingChanges.AnyAsync(x => x.DedupKey == key, ct))
            db.SchedulingChanges.Add(SchedulingChange.Record(sessionId, type, providerIdentity, clock.UtcNow));
    }
}

public sealed class UnmatchedCalendlyEventTypeException(string eventTypeUri)
    : Exception($"Calendly event type is not mapped: {eventTypeUri}");
