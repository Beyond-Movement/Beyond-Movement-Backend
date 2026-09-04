using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.TimeZone;

/// <summary>
/// Keeps the stored zone in step with the device it was last used from.
/// <para>
/// This is a synchronisation endpoint, not a setting. The app detects the zone, compares it with
/// the one <c>/auth/me</c> returned and calls this only on a difference — so in normal operation
/// it runs once, when the coach's device has actually moved.
/// </para>
/// </summary>
public sealed class UpdateTimeZoneHandler(
    IIdentityDbContext db,
    IClock clock,
    ILogger<UpdateTimeZoneHandler> logger)
{
    public async Task<Result<TimeZoneResponse>> HandleAsync(
        Guid userId, UpdateTimeZoneRequest request, CancellationToken ct = default)
    {
        if (!TimeZoneId.TryNormalize(request.TimeZone, out var timeZone))
            return Result<TimeZoneResponse>.Failure(IdentityErrors.TimeZoneInvalid);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || user.Status == UserStatus.Deleted)
            return Result<TimeZoneResponse>.Failure(IdentityErrors.InvalidCredentials);

        // Idempotent by construction. The app should not call this when the zone already
        // matches, but a client that does must not churn UpdatedAtUtc on every launch.
        if (string.Equals(user.TimeZone, timeZone, StringComparison.Ordinal))
            return Result<TimeZoneResponse>.Success(new TimeZoneResponse(user.TimeZone));

        var previous = user.TimeZone;
        user.SetTimeZone(timeZone, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        // Worth a log line: this silently changes which UTC range every dashboard period covers,
        // so a coach reporting "my week looks wrong" is answered by finding this.
        logger.LogInformation(
            "Time zone for user {UserId} changed from {PreviousTimeZone} to {TimeZone}",
            user.Id, previous, user.TimeZone);

        return Result<TimeZoneResponse>.Success(new TimeZoneResponse(user.TimeZone));
    }
}
