using System.Security.Claims;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling;
using BeyondMovement.Modules.Scheduling.Calendly;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Features;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

public static class SchedulingEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        var booking = app.MapGroup("/api/v1/scheduling").WithTags("Scheduling");
        booking.MapGet("/session-types", GetTypes).RequireAuthorization("AthleteOnly");
        booking.MapGet("/session-types/{eventTypeId}/availability", GetAvailability).RequireAuthorization("AthleteOnly");
        booking.MapPost("/bookings", Book).RequireAuthorization("AthleteOnly");

        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Sessions");
        sessions.MapGet(string.Empty, List);
        sessions.MapGet("/upcoming", Upcoming);
        sessions.MapGet("/{id:guid}", Detail);
        sessions.MapPost("/{id:guid}/cancel", Cancel);
        sessions.MapGet("/{id:guid}/reschedule", Reschedule);

        app.MapPost("/api/v1/webhooks/calendly", ReceiveWebhook)
            .AllowAnonymous().WithTags("Calendly webhooks")
            .DisableAntiforgery();
        app.MapPost("/api/v1/scheduling/refresh", (ISchedulingJobScheduler scheduler) =>
        {
            scheduler.EnqueueReconciliation();
            return Results.Accepted();
        }).RequireAuthorization("AdminOnly").WithTags("Scheduling");
        return app;
    }

    private static async Task<IResult> GetTypes(SchedulingService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.GetBookableTypesAsync(ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> GetAvailability(string eventTypeId, DateTime fromUtc, DateTime toUtc,
        SchedulingService service, HttpContext http, CancellationToken ct)
    {
        var result = await service.GetAvailabilityAsync(eventTypeId, fromUtc, toUtc, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Book(BookSessionRequest request, IValidator<BookSessionRequest> validator,
        SchedulingService service, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return SchedulingErrors.IdempotencyKeyRequired.ToProblem(http);
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();
        var athlete = await db.AthleteProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.CoachId == coachId, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (athlete is null || user?.FullName is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        var result = await service.BookAsync(coachId, athlete.Id, user.FullName, user.Email, idempotencyKey, request, ct);
        return result.IsSuccess ? Results.Created($"/api/v1/sessions/{result.Value.Id}", result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> List(DateTime? fromUtc, DateTime? toUtc, Guid? athleteProfileId,
        SessionStatus? status, int limit, string? cursor, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();
        var take = Math.Clamp(limit == 0 ? 30 : limit, 1, 100);
        var query = db.Sessions.AsNoTracking().Where(x => x.CoachId == coachId);
        if (principal.IsInRole(nameof(UserRole.Athlete)))
        {
            var ownId = await db.AthleteProfiles.Where(x => x.UserId == userId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
            if (ownId is null) return Results.Ok(new SessionPage([], null));
            query = query.Where(x => x.AthleteProfileId == ownId);
        }
        else if (athleteProfileId is not null) query = query.Where(x => x.AthleteProfileId == athleteProfileId);
        if (fromUtc is not null) query = query.Where(x => x.ScheduledStartUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.ScheduledStartUtc < toUtc);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (DateTime.TryParse(cursor, out var before)) query = query.Where(x => x.ScheduledStartUtc > before.ToUniversalTime());
        var rows = await query.OrderBy(x => x.ScheduledStartUtc).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? rows[take - 1].ScheduledStartUtc.ToString("O") : null;
        return Results.Ok(new SessionPage(rows.Take(take).Select(x => x.ToResponse()).ToArray(), next));
    }

    private static Task<IResult> Upcoming(int limit, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        List(clock.UtcNow, null, null, SessionStatus.Scheduled, limit == 0 ? 10 : limit, null, db, principal, ct);

    private static async Task<IResult> Detail(Guid id, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct);
        return session is null ? SchedulingErrors.SessionNotFound.ToProblem(http) : Results.Ok(session.ToResponse());
    }

    private static async Task<IResult> Cancel(Guid id, CancelSessionRequest request, SchedulingService service,
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct, tracked: true);
        if (session is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        var result = await service.CancelAsync(session, request.Reason, ct);
        return result.IsSuccess ? Results.Ok(session.ToResponse()) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Reschedule(Guid id, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var session = await OwnedSession(id, db, principal, ct);
        if (session?.RescheduleUrl is null) return SchedulingErrors.SessionNotFound.ToProblem(http);
        return Results.Ok(new { url = session.RescheduleUrl });
    }

    private static async Task<IResult> ReceiveWebhook(HttpRequest request, AppDbContext db,
        ICalendlyWebhookVerifier verifier, ICalendlyWebhookParser parser, ISchedulingJobScheduler scheduler,
        IClock clock, HttpContext http, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync(ct);
        if (!verifier.IsValid(json, request.Headers["Calendly-Webhook-Signature"].FirstOrDefault(), clock.UtcNow))
            return SchedulingErrors.CalendlySignatureInvalid.ToProblem(http);
        CalendlyWebhookEnvelope envelope;
        try { envelope = parser.Parse(json); }
        catch (JsonException) { return Results.BadRequest(); }
        if (await db.CalendlyWebhookEvents.AnyAsync(x => x.IdempotencyKey == envelope.IdempotencyKey, ct)) return Results.Ok();
        db.CalendlyWebhookEvents.Add(CalendlyWebhookEvent.Receive(envelope.IdempotencyKey, envelope.EventType, json, clock.UtcNow));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Ok(); }
        scheduler.EnqueueWebhook(db.Entry(db.CalendlyWebhookEvents.Local.Single(x => x.IdempotencyKey == envelope.IdempotencyKey)).Entity.Id);
        return Results.Accepted();
    }

    private static async Task<Session?> OwnedSession(Guid id, AppDbContext db, ClaimsPrincipal principal,
        CancellationToken ct, bool tracked = false)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return null;
        var query = tracked ? db.Sessions.AsQueryable() : db.Sessions.AsNoTracking();
        query = query.Where(x => x.Id == id && x.CoachId == coachId);
        if (principal.IsInRole(nameof(UserRole.Athlete)))
            query = query.Where(x => db.AthleteProfiles.Any(a => a.Id == x.AthleteProfileId && a.UserId == userId));
        return await query.SingleOrDefaultAsync(ct);
    }
}
